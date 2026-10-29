"""Linux-only helper ownership tests with a tiny C socket fixture, never AI/audio/WSL.

Run directly with Python on Linux. Every process, model-path placeholder and PID
marker is confined to a TemporaryDirectory; no existing service is inspected or
stopped. The fixture binds only an OS-selected IPv4 loopback port.
"""
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import time
import unittest
import uuid


ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Packaging" / "scripts"
GROUPS = ROOT / "Packaging" / "vibevoice"
FIXTURE = r"""
#include <arpa/inet.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>
static volatile sig_atomic_t running = 1;
static void stopped(int sig) { (void)sig; running = 0; }
int main(int argc, char **argv) {
    int port = 0;
    for (int i = 1; i + 1 < argc; ++i)
        if (strcmp(argv[i], "--port") == 0) port = atoi(argv[i + 1]);
    if (port < 1024 || port > 65535) return 2;
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in address = {0};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons((unsigned short)port);
    if (fd < 0 || bind(fd, (struct sockaddr *)&address, sizeof(address)) || listen(fd, 1)) return 7;
    signal(SIGTERM, stopped); signal(SIGINT, stopped);
    puts("FAKE_SERVER_BOUND"); fflush(stdout);
    struct timespec delay = {0, 10000000};
    while (running) nanosleep(&delay, NULL);
    close(fd);
    return 0;
}
"""


@unittest.skipUnless(sys.platform == "linux" and shutil.which("cc") and shutil.which("bash"),
                     "Linux /proc, bash and a C compiler are required; no WSL is started")
class ManagedPortScriptTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="saywhat-owned-port-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.env = dict(os.environ, XDG_DATA_HOME=str(self.directory))
        self.owner = uuid.uuid4().hex
        self.server = self.directory / "dummy-server"
        source = self.directory / "fixture.c"
        source.write_text(FIXTURE)
        subprocess.run(["cc", "-std=c11", "-D_POSIX_C_SOURCE=200809L", str(source), "-o", str(self.server)],
                       check=True, timeout=20, capture_output=True)
        self.model = self.directory / "placeholder.gguf"
        self.model.write_text("Not model weights. The socket fixture ignores this path.")

    def spawn(self, arguments, **kwargs):
        process = subprocess.Popen(arguments, env=self.env, **kwargs)
        self.addCleanup(self.stop_retained_process, process)
        return process

    @staticmethod
    def stop_retained_process(process):
        if process.poll() is None:
            process.send_signal(signal.SIGTERM)
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        for stream in (process.stdout, process.stderr):
            if stream:
                stream.close()

    @staticmethod
    def unused_port():
        with socket.socket() as candidate:
            candidate.bind(("127.0.0.1", 0))
            return candidate.getsockname()[1]

    @staticmethod
    def wait_for(predicate, seconds=5):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if predicate():
                return
            time.sleep(.03)
        raise AssertionError("The isolated fixture did not reach its expected state.")

    def call(self, script, *arguments):
        return subprocess.run(["bash", str(script), *map(str, arguments)], env=self.env,
                              timeout=10, capture_output=True, text=True)

    def test_all_helpers_reject_invalid_ports_before_preparation_or_startup(self):
        for port in ("0", "1023", "65536", "-1", "+25347", "025347", "25347;echo", "25347 ", "9" * 100):
            commands = (
                (SCRIPTS / "run-voxtral.sh", self.server, self.model, "cpu", port, self.owner),
                (SCRIPTS / "stop-voxtral.sh", self.server, port, self.owner),
                (GROUPS / "run-vibevoice.sh", self.directory, "microsoft/VibeVoice-ASR-Streaming-1.5B", self.owner, port),
                (GROUPS / "stop-vibevoice.sh", self.owner, port),
            )
            for script, *arguments in commands:
                with self.subTest(script=script.name, port=port):
                    self.assertEqual(self.call(script, *arguments).returncode, 2)
        self.assertFalse((self.directory / "localtalk").exists())
        self.assertFalse((self.directory / "saywhat").exists())

    def test_voxtral_proof_comes_from_owned_nondefault_listener_and_cleanup_keeps_port(self):
        port = self.unused_port()
        output = self.directory / "owned-output.txt"
        with output.open("w") as writer:
            process = self.spawn(["bash", str(SCRIPTS / "run-voxtral.sh"), str(self.server), str(self.model), "cpu",
                                  str(port), self.owner], stdout=writer, stderr=subprocess.STDOUT)
            expected = f"SAYWHAT_OWNED_LISTENER {self.owner} {port}"
            self.wait_for(lambda: expected in output.read_text().splitlines())
            self.assertIsNone(process.poll())
            marker = self.directory / "localtalk" / "pids" / f"voxtral-{port}-{self.owner}.pid"
            self.assertTrue(marker.is_file())
            self.assertEqual(self.call(SCRIPTS / "stop-voxtral.sh", self.server, port, self.owner).returncode, 0)
            self.assertEqual(process.wait(timeout=10), 0)
            self.assertFalse(marker.exists())

    def test_foreign_listener_does_not_produce_owned_proof_or_get_stopped(self):
        port = self.unused_port()
        foreign = self.spawn([str(self.server), "--port", str(port)], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.assertEqual(foreign.stdout.readline().strip(), b"FAKE_SERVER_BOUND")
        result = self.call(SCRIPTS / "run-voxtral.sh", self.server, self.model, "cpu", port, self.owner)
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("SAYWHAT_OWNED_LISTENER", result.stdout)
        self.assertIn(f"LOCALTALK_PORT_CONFLICT={self.owner}:{port}", result.stdout.splitlines())
        self.assertIsNone(foreign.poll())
        self.assertFalse((self.directory / "localtalk" / "pids" / f"voxtral-{port}-{self.owner}.pid").exists())
        groups = self.call(GROUPS / "run-vibevoice.sh", self.directory, "microsoft/VibeVoice-ASR-Streaming-1.5B", self.owner, port)
        self.assertEqual(groups.returncode, 6)
        self.assertIn(f"LOCALTALK_PORT_CONFLICT={self.owner}:{port}", groups.stdout.splitlines())
        self.assertIsNone(foreign.poll())

    def test_groups_cleanup_rechecks_owner_and_selected_port(self):
        # Force only this helper's uid branch to a fixture-owned runtime, even
        # under a root CI container. No /opt or home directory is changed.
        tools = self.directory / "tools"
        tools.mkdir()
        fake_id = tools / "id"
        fake_id.write_text("#!/bin/sh\nprintf '1000\\n'\n")
        fake_id.chmod(0o755)
        self.env["PATH"] = str(tools) + os.pathsep + self.env["PATH"]
        revision = re.search(r"^revision=([a-f0-9]{40})$", (GROUPS / "stop-vibevoice.sh").read_text(), re.M)[1]
        interpreter = self.directory / "saywhat" / "vibevoice" / revision / "venv" / "bin" / "python"
        interpreter.parent.mkdir(parents=True)
        interpreter.symlink_to(self.server)
        port = self.unused_port()
        process = self.spawn([str(interpreter), str(GROUPS / "server.py"), "--owner", self.owner, "--port", str(port)],
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.assertEqual(process.stdout.readline().strip(), b"FAKE_SERVER_BOUND")
        birth = Path(f"/proc/{process.pid}/stat").read_text().split()[21]
        marker_root = self.directory / "saywhat" / "pids"
        marker_root.mkdir()
        marker = marker_root / f"vibevoice-{port}-{self.owner}.pid"
        def write_marker(path, owner):
            path.write_text(f"{process.pid}\n{birth}\n{owner}\n{GROUPS / 'server.py'}\n")
        write_marker(marker, uuid.uuid4().hex)
        self.assertEqual(self.call(GROUPS / "stop-vibevoice.sh", self.owner, port).returncode, 3)
        self.assertIsNone(process.poll())
        write_marker(marker, self.owner)
        wrong_port = 1024 if port != 1024 else 1025
        wrong_marker = marker_root / f"vibevoice-{wrong_port}-{self.owner}.pid"
        write_marker(wrong_marker, self.owner)
        self.assertEqual(self.call(GROUPS / "stop-vibevoice.sh", self.owner, wrong_port).returncode, 4)
        self.assertIsNone(process.poll())
        self.assertTrue(marker.exists())
        self.assertTrue(wrong_marker.exists())
        self.assertEqual(self.call(GROUPS / "stop-vibevoice.sh", self.owner, port).returncode, 0)
        self.assertEqual(process.wait(timeout=10), 0)
        self.assertFalse(marker.exists())


if __name__ == "__main__":
    unittest.main()
