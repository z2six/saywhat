#!/usr/bin/env python3
"""Promote one pinned hosted candidate; never rebuild or execute its contents."""
import hashlib
import http.client
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
import time
from urllib.parse import quote, urlsplit
import zipfile

REPOSITORY = "z2six/saywhat"
VERSION = "0.2.11"
TAG = "v" + VERSION
SOURCE = "ced4a843dd83f5f8e02b5d05d9132c724cb4a326"
RUN = 40954411311
ARTIFACT = 12932109704
ARCHIVE_BYTES = 145513508
ARCHIVE_SHA = "54e5d8451259e7e5ca01d102ead77b69427a9431d8b734e8c3750918f34512ac"
INSTALLER = "SayWhat-0.2.11-Setup.exe"
INSTALLER_BYTES = 134938665
INSTALLER_SHA = "257952b8ad62d4fbb40146b1234cd417d7eb34f02163d645920e243284ee0ae9"
SOURCE_FILE = "SayWhat-Corresponding-Source.zip"
SOURCE_BYTES = 4977909
SOURCE_SHA = "698e5f80a5cfb4034174337a483c80866325cea134b7f9d55a7101e481a5e94e"
RAW_DLL_SHA = "02e6fa8ed14f49bf697982efc99554ce83f5c93d7e428e15fe82b6b1e9f6dc58"
COMPLETE = "SayWhat-0.2.11-Complete.zip"
CHECKSUMS = "SHA256SUMS-0.2.11.txt"
PACKAGE_TOP = {INSTALLER, SOURCE_FILE, "DISTRIBUTION-NOTES.md", "LICENSE", "README.md",
               "release-manifest.json", "SHA256SUMS.txt", "SOURCE-AND-LICENSES.md", "THIRD-PARTY-NOTICES.md"}
WARNING = "Bitdefender Gen:Variant.MSILHeracles.276181; vendor review submitted and pending."
NOTES = f"""## Known antivirus warning

Bitdefender currently flags this build as **Gen:Variant.MSILHeracles.276181**. The same detection affected the CI-built DLL. A review has been submitted to Bitdefender and is still pending; this release is not an antivirus clearance. Keep antivirus protection enabled. If your security software blocks the download or installation, wait for the review or use an earlier version. Do not disable protection or add an exclusion to install this preview.

## Changes

SayWhat? now chooses available local ports for its built-in speech and translation servers. Other apps can keep using former defaults such as port 8080. Reconnection and both translation directions follow the chosen session addresses. VRChat OSC and configured external translator connections are unchanged.

Fixed ports remain available under Advanced → Manual setup → Connection settings. Startup verifies the exact owned process before accepting readiness, retries confirmed port conflicts, and stops only app-owned tools during cleanup.

This unsigned Windows x64 preview includes its matching GPLv3 corresponding source, licenses and SHA-256 checksums. The installer was built and tested in the existing hosted candidate run; promotion does not rebuild or run it. The included package manifest records its pre-publication build validation.

Source commit: `{SOURCE}`

Candidate run: https://github.com/{REPOSITORY}/actions/runs/{RUN}
"""


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def safe_name(name):
    require(isinstance(name, str) and 0 < len(name) <= 1024 and "\\" not in name and ":" not in name and "\0" not in name,
            "Unsafe archive path")
    parts = name.rstrip("/").split("/")
    require(not name.startswith("/") and all(part not in ("", ".", "..") for part in parts), "Unsafe archive path")
    require(str(PurePosixPath(name.rstrip("/"))) == name.rstrip("/"), "Noncanonical archive path")
    return name


def zip_inventory(archive):
    entries = {}
    total = 0
    infos = archive.infolist()
    require(0 < len(infos) <= 4096, "Unexpected ZIP entry count")
    seen = set()
    for info in infos:
        safe_name(info.filename)
        folded = info.filename.rstrip("/").casefold()
        require(folded not in seen, "Duplicate ZIP path")
        seen.add(folded)
        require(not info.flag_bits & 1 and not stat.S_ISLNK(info.external_attr >> 16), "Encrypted or linked ZIP entry")
        require(info.file_size <= 160 * 1024 * 1024, "ZIP entry exceeds bound")
        total += info.file_size
        require(total <= 300 * 1024 * 1024, "ZIP expansion exceeds bound")
        if info.is_dir():
            continue
        digest = hashlib.sha256()
        count = 0
        with archive.open(info) as stream:
            while block := stream.read(1024 * 1024):
                count += len(block)
                require(count <= info.file_size, "ZIP entry length exceeded")
                digest.update(block)
        require(count == info.file_size, "Incomplete ZIP entry")
        entries[info.filename] = {"Bytes": count, "Sha256": digest.hexdigest()}
    return entries


def checksum_entries(text):
    result = {}
    for line in text.splitlines():
        match = re.fullmatch(r"([a-f0-9]{64})  (.+)", line)
        require(match is not None, "Malformed checksum line")
        name = safe_name(match[2])
        require(name not in result, "Duplicate checksum path")
        result[name] = match[1]
    return result


def manifest_entries(files):
    require(isinstance(files, list) and 0 < len(files) <= 4096, "Invalid manifest inventory")
    result = {}
    for item in files:
        name = safe_name(item["Path"])
        require(name not in result and type(item["Bytes"]) is int and item["Bytes"] >= 0 and
                re.fullmatch(r"[a-f0-9]{64}", item["Sha256"]) is not None, "Invalid manifest entry")
        result[name] = {"Bytes": item["Bytes"], "Sha256": item["Sha256"]}
    return result


class Github:
    def __init__(self, token):
        self.token = token

    def request(self, method, path, payload=None, expected=(200,), host="api.github.com", upload=None):
        require(host in ("api.github.com", "uploads.github.com") and path.startswith("/"), "Unexpected API target")
        headers = {"Authorization": "Bearer " + self.token, "Accept": "application/vnd.github+json",
                   "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "SayWhat-reviewed-candidate-promotion"}
        body = None
        if payload is not None:
            body = json.dumps(payload).encode("utf-8")
            headers["Content-Type"] = "application/json"
        if upload is not None:
            body = upload.open("rb")
            headers.update({"Content-Type": "application/octet-stream", "Content-Length": str(upload.stat().st_size)})
        connection = http.client.HTTPSConnection(host, timeout=180)
        try:
            connection.request(method, path, body=body, headers=headers)
            response = connection.getresponse()
            status, location = response.status, response.getheader("Location")
            data = response.read(2 * 1024 * 1024 + 1)
            require(len(data) <= 2 * 1024 * 1024, "Oversized GitHub API response")
            require(status in expected, f"GitHub API {method} failed with HTTP {status}")
            # Redirect bodies are not an API JSON contract; storage is requested
            # separately, without Authorization, below.
            return status, json.loads(data) if data and status != 302 else None, location
        finally:
            if upload is not None and body is not None:
                body.close()
            connection.close()

    def api(self, method, suffix, payload=None, expected=(200,)):
        return self.request(method, f"/repos/{REPOSITORY}" + suffix, payload, expected)[1]

    def download_candidate(self, destination):
        _, _, location = self.request("GET", f"/repos/{REPOSITORY}/actions/artifacts/{ARTIFACT}/zip", expected=(302,))
        uri = urlsplit(location or "")
        require(uri.scheme == "https" and uri.port in (None, 443) and uri.username is None and uri.hostname is not None and
                (uri.hostname.endswith(".blob.core.windows.net") or uri.hostname.endswith(".actions.githubusercontent.com") or
                 uri.hostname.endswith(".githubusercontent.com")), "Unexpected artifact storage redirect")
        # Do not forward the GitHub token to the signed artifact storage URL.
        connection = http.client.HTTPSConnection(uri.hostname, timeout=180)
        digest, count = hashlib.sha256(), 0
        try:
            connection.request("GET", uri.path + ("?" + uri.query if uri.query else ""))
            response = connection.getresponse()
            require(response.status == 200 and int(response.getheader("Content-Length", "0")) == ARCHIVE_BYTES, "Candidate download size/status mismatch")
            with destination.open("xb") as target:
                while block := response.read(1024 * 1024):
                    count += len(block)
                    require(count <= ARCHIVE_BYTES, "Oversized candidate download")
                    digest.update(block)
                    target.write(block)
        finally:
            connection.close()
        require(count == ARCHIVE_BYTES and digest.hexdigest() == ARCHIVE_SHA, "Candidate ZIP SHA-256 mismatch")


def verify_candidate(path, output):
    with zipfile.ZipFile(path) as archive:
        inventory = zip_inventory(archive)
        require("candidate-manifest.json" in inventory and inventory["candidate-manifest.json"]["Bytes"] <= 512 * 1024,
                "Missing or oversized candidate manifest")
        manifest = json.loads(archive.read("candidate-manifest.json"))
        require(manifest["SchemaVersion"] == 1 and manifest["App"] == "SayWhat?" and manifest["Version"] == VERSION and
                manifest["SourceCommit"] == SOURCE and manifest["Status"] == "unreleased-review-candidate" and
                manifest["Published"] is False and manifest["ModelsBundled"] is False and manifest["UserDataIncluded"] is False,
                "Candidate provenance mismatch")
        expected = manifest_entries(manifest["Files"])
        require(set(inventory) == set(expected) | {"candidate-manifest.json", "SHA256SUMS.txt"}, "Candidate contains missing or extra files")
        require(all(inventory[name] == value for name, value in expected.items()), "Candidate file inventory mismatch")
        require(checksum_entries(archive.read("SHA256SUMS.txt").decode("utf-8-sig")) ==
                {name: item["Sha256"] for name, item in inventory.items() if name != "SHA256SUMS.txt"}, "Candidate checksum inventory mismatch")
        require(manifest["InstallerSha256"] == INSTALLER_SHA and manifest["CorrespondingSourceSha256"] == SOURCE_SHA and
                manifest["RawAppDllSha256"] == RAW_DLL_SHA and inventory["diagnostics/SayWhat.dll"] == {"Bytes": 4842496, "Sha256": RAW_DLL_SHA},
                "Pinned binary/source identity mismatch")
        package = {name[len("package/"):]: value for name, value in inventory.items() if name.startswith("package/")}
        require({name for name in package if "/" not in name} == PACKAGE_TOP and
                all("/" not in name or name.startswith("licenses/") for name in package), "Unexpected package contents")
        require(package[INSTALLER] == {"Bytes": INSTALLER_BYTES, "Sha256": INSTALLER_SHA} and
                package[SOURCE_FILE] == {"Bytes": SOURCE_BYTES, "Sha256": SOURCE_SHA}, "Pinned installer/source sizes differ")
        require(checksum_entries(archive.read("package/SHA256SUMS.txt").decode("utf-8-sig")) ==
                {name: item["Sha256"] for name, item in package.items() if name != "SHA256SUMS.txt"}, "Package checksum inventory mismatch")
        released = json.loads(archive.read("package/release-manifest.json"))
        require(released["Version"] == VERSION and released["Installer"]["File"] == INSTALLER and
                released["Installer"]["Bytes"] == INSTALLER_BYTES and released["Installer"]["Sha256"] == INSTALLER_SHA and
                released["CorrespondingSource"]["File"] == SOURCE_FILE and released["CorrespondingSource"]["Bytes"] == SOURCE_BYTES and
                released["CorrespondingSource"]["Sha256"] == SOURCE_SHA and released["OriginalContributionLicense"] == "GPL-3.0-only",
                "Exported binary/source/license pair differs")
        require(manifest_entries(released["Files"]) == {name: item for name, item in package.items()
                if name not in ("release-manifest.json", "SHA256SUMS.txt")}, "Export file inventory mismatch")
        with zipfile.ZipFile(io.BytesIO(archive.read("package/" + SOURCE_FILE))) as source:
            require(zip_inventory(source) == manifest_entries(released["CorrespondingSource"]["Files"]), "Corresponding source inventory mismatch")
        output.mkdir()
        for name in package:
            target = output.joinpath(*PurePosixPath(name).parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.open("package/" + name) as stream, target.open("xb") as written:
                while block := stream.read(1024 * 1024):
                    written.write(block)
    # A complete distribution is assembled only from unchanged package files.
    complete = output.parent / COMPLETE
    with zipfile.ZipFile(complete, "x", compression=zipfile.ZIP_STORED) as distribution:
        for name in sorted(package):
            info = zipfile.ZipInfo(name, date_time=(2000, 1, 1, 0, 0, 0))
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            with output.joinpath(*PurePosixPath(name).parts).open("rb") as source, distribution.open(info, "w") as target:
                while block := source.read(1024 * 1024):
                    target.write(block)
    with zipfile.ZipFile(complete) as distribution:
        require(zip_inventory(distribution) == package, "Complete ZIP differs from unchanged package")
    assets = [output / name for name in sorted(PACKAGE_TOP)] + [complete]
    checksum = output.parent / CHECKSUMS
    checksum.write_text("".join(file_sha(file) + "  " + file.name + "\n" for file in assets), encoding="utf-8")
    return assets + [checksum]


def file_sha(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(1024 * 1024):
            digest.update(block)
    return digest.hexdigest()


def main():
    require(os.environ.get("GITHUB_ACTIONS") == "true" and os.environ.get("GITHUB_REPOSITORY") == REPOSITORY and
            os.environ.get("GITHUB_REF") == "refs/heads/main" and
            os.environ.get("GITHUB_EVENT_NAME") == "workflow_dispatch" and os.environ.get("SAYWHAT_ACKNOWLEDGE_WARNING") == "true",
            "Promotion requires the acknowledged manual workflow in the project repository")
    token = os.environ.get("GH_TOKEN", "")
    require(bool(token), "Missing workflow token")
    github = Github(token)
    run = github.api("GET", f"/actions/runs/{RUN}")
    require(run["id"] == RUN and run["head_sha"] == SOURCE and run["status"] == "completed" and run["conclusion"] == "success" and
            run["head_repository"]["full_name"] == REPOSITORY and run["path"] == ".github/workflows/release-candidate.yml", "Candidate workflow provenance mismatch")
    artifact = github.api("GET", f"/actions/artifacts/{ARTIFACT}")
    require(artifact["id"] == ARTIFACT and artifact["expired"] is False and artifact["size_in_bytes"] == ARCHIVE_BYTES and
            artifact["workflow_run"]["id"] == RUN and artifact["workflow_run"]["head_sha"] == SOURCE and
            artifact.get("digest") == "sha256:" + ARCHIVE_SHA, "Pinned artifact metadata mismatch")
    output = Path("artifacts/promote-0.2.11")
    output.mkdir(parents=True, exist_ok=False)
    archive = output / "verified-candidate.zip"
    github.download_candidate(archive)
    assets = verify_candidate(archive, output / "package")
    expected = {file.name: {"name": file.name, "sizeBytes": file.stat().st_size, "sha256": file_sha(file)} for file in assets}
    require(len(expected) == 11, "Unexpected release asset set")

    def verify_tag():
        reference = github.api("GET", "/git/ref/tags/" + TAG, expected=(200, 404))
        if reference is None or "object" not in reference:
            github.api("POST", "/git/refs", {"ref": "refs/tags/" + TAG, "sha": SOURCE}, expected=(201,))
            reference = github.api("GET", "/git/ref/tags/" + TAG)
        target = reference["object"]
        for _ in range(3):
            if target["type"] != "tag":
                break
            target = github.api("GET", "/git/tags/" + target["sha"])["object"]
        require(target["type"] == "commit" and target["sha"] == SOURCE, "Release tag does not target the verified source commit")

    verify_tag()
    release = github.api("GET", "/releases/tags/" + TAG, expected=(200, 404))
    if release is None or "id" not in release:
        release = github.api("POST", "/releases", {"tag_name": TAG, "target_commitish": SOURCE, "name": "SayWhat? 0.2.11 preview",
            "body": NOTES, "draft": True, "prerelease": True, "make_latest": "false"}, expected=(201,))
    require(release["draft"] is True and release["prerelease"] is True and release["tag_name"] == TAG and
            release["target_commitish"] == SOURCE and release["body"] == NOTES, "Existing release is not this unchanged promotion draft")
    release_id = release["id"]

    def uploaded_assets():
        items = github.api("GET", f"/releases/{release_id}/assets?per_page=100")
        require(len(items) <= 20 and len({item["name"] for item in items}) == len(items), "Unexpected or duplicate uploaded assets")
        require(all(item["name"] in expected for item in items), "Draft has an unrelated asset; refusing replacement")
        return {item["name"]: item for item in items}

    current = uploaded_assets()
    for file in assets:
        if file.name not in current:
            github.request("POST", f"/repos/{REPOSITORY}/releases/{release_id}/assets?name=" + quote(file.name, safe=""),
                           expected=(201,), host="uploads.github.com", upload=file)
        else:
            require(current[file.name]["size"] == expected[file.name]["sizeBytes"] and
                    current[file.name].get("digest") == "sha256:" + expected[file.name]["sha256"] and current[file.name]["state"] == "uploaded",
                    "Existing draft asset differs; it will not be replaced")
    verified = False
    for _ in range(20):
        current = uploaded_assets()
        verified = set(current) == set(expected) and all(current[name]["state"] == "uploaded" and
            current[name]["size"] == item["sizeBytes"] and current[name].get("digest") == "sha256:" + item["sha256"] for name, item in expected.items())
        if verified:
            break
        time.sleep(3)
    require(verified, "GitHub did not confirm every uploaded asset size and SHA-256; draft left unpublished")
    verify_tag()
    release = github.api("PATCH", f"/releases/{release_id}", {"draft": False, "prerelease": True, "make_latest": "false"})
    require(release["draft"] is False and release["prerelease"] is True and release["published_at"], "Publication did not complete")
    current = uploaded_assets()
    require(set(current) == set(expected) and all(current[name]["size"] == item["sizeBytes"] and
        current[name].get("digest") == "sha256:" + item["sha256"] for name, item in expected.items()), "Post-publication asset mismatch")
    receipt = {"schemaVersion": 1, "version": VERSION, "sourceCommit": SOURCE, "candidateRunId": RUN, "candidateArtifactId": ARTIFACT,
        "candidateArchiveSha256": ARCHIVE_SHA, "tag": TAG, "publishedAt": release["published_at"], "releaseURL": release["html_url"],
        "knownAVStatus": {"detected": True, "product": "Bitdefender", "signature": "Gen:Variant.MSILHeracles.276181", "review": "submitted-pending", "advice": "Keep antivirus enabled; wait or use an earlier version if blocked."},
        "installer": expected[INSTALLER], "source": expected[SOURCE_FILE], "distribution": expected[COMPLETE],
        "assets": [dict(expected[name], id=current[name]["id"], digest=current[name]["digest"], url=current[name]["browser_download_url"]) for name in sorted(expected)]}
    (output / "promotion-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print("Verified and published " + release["html_url"])


def self_test():
    checks = 0
    for name in ("package/LICENSE", "licenses/example.txt", ".github/workflows/ci.yml"):
        safe_name(name)
        checks += 1
    for name in ("../secret", "/absolute", "C:/private", "package\\file", "package//file", "x/./file", "x/../file", "nul\0file"):
        try:
            safe_name(name)
        except RuntimeError:
            checks += 1
        else:
            raise RuntimeError("Unsafe fixture path accepted")
    content = b"synthetic fixture, not an executable"
    data = io.BytesIO()
    with zipfile.ZipFile(data, "w") as archive:
        archive.writestr("package/LICENSE", content)
    data.seek(0)
    with zipfile.ZipFile(data) as archive:
        require(zip_inventory(archive) == {"package/LICENSE": {"Bytes": len(content), "Sha256": sha(content)}}, "ZIP fixture failed")
    checks += 1
    require(checksum_entries(sha(content) + "  package/LICENSE\n") == {"package/LICENSE": sha(content)}, "Checksum fixture failed")
    checks += 1
    for text in ("bad checksum", sha(content) + "  ../private", (sha(content) + "  x\n") * 2):
        try:
            checksum_entries(text)
        except RuntimeError:
            checks += 1
        else:
            raise RuntimeError("Invalid checksum fixture accepted")
    for names, link in ((("safe", "SAFE"), False), (("../private",), False), (("safe",), True)):
        data = io.BytesIO()
        with zipfile.ZipFile(data, "w") as archive:
            for name in names:
                info = zipfile.ZipInfo(name)
                info.external_attr = ((stat.S_IFLNK if link else stat.S_IFREG) | 0o644) << 16
                archive.writestr(info, content)
        data.seek(0)
        try:
            with zipfile.ZipFile(data) as archive:
                zip_inventory(archive)
        except RuntimeError:
            checks += 1
        else:
            raise RuntimeError("Unsafe ZIP fixture accepted")
    good = [{"Path": "LICENSE", "Bytes": len(content), "Sha256": sha(content)}]
    require(manifest_entries(good) == {"LICENSE": {"Bytes": len(content), "Sha256": sha(content)}}, "Manifest fixture failed")
    checks += 1
    for entries in (good + good, [{"Path": "../private", "Bytes": 1, "Sha256": sha(content)}],
                    [{"Path": "LICENSE", "Bytes": -1, "Sha256": sha(content)}]):
        try:
            manifest_entries(entries)
        except RuntimeError:
            checks += 1
        else:
            raise RuntimeError("Invalid manifest fixture accepted")
    print(f"PASS {checks} promotion validation checks; no network or binaries used")


if __name__ == "__main__":
    try:
        if sys.argv[1:] == ["--self-test"]:
            self_test()
        else:
            require(len(sys.argv) == 1, "Unexpected promotion arguments")
            main()
    except Exception as error:
        print(type(error).__name__ + ": " + str(error), file=sys.stderr)
        sys.exit(1)
