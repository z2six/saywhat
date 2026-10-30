#!/usr/bin/env python3
"""Promote a completely pinned hosted candidate; never rebuild or execute it."""
import argparse
import copy
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
import tempfile
from urllib.parse import quote, urlsplit
import zipfile

REPOSITORY = "z2six/saywhat"
ROOT = Path(__file__).resolve().parent.parent
MIB = 1024 * 1024
VERSION_PATTERN = r"(?:0|[1-9][0-9]{0,2})\.(?:0|[1-9][0-9]{0,2})\.(?:0|[1-9][0-9]{0,2})"


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def exact_keys(value, keys, description):
    require(type(value) is dict and set(value) == set(keys), "Unexpected " + description + " fields")


def strict_json(data):
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, "Duplicate JSON key")
            result[key] = value
        return result
    def invalid_constant(_):
        raise RuntimeError("Non-finite JSON value")
    return json.loads(data, object_pairs_hook=pairs, parse_constant=invalid_constant)


def pinned_sha(value, description):
    require(type(value) is str and re.fullmatch(r"[a-f0-9]{64}", value) is not None and value != "0" * 64,
            description + " SHA-256 is not pinned")


def positive_int(value, maximum, description):
    require(type(value) is int and 0 < value <= maximum, description + " is not a positive bounded pin")


def validate_pins(pins, version):
    require(type(version) is str and re.fullmatch(VERSION_PATTERN, version) is not None, "Invalid explicit version")
    exact_keys(pins, {"schemaVersion", "repository", "version", "sourceCommit", "candidateRunId", "candidateArtifactId",
                      "candidateArchive", "installer", "correspondingSource", "rawAppDll", "releaseNotesSha256", "antivirus"}, "candidate pin manifest")
    require(type(pins["schemaVersion"]) is int and pins["schemaVersion"] == 1 and pins["repository"] == REPOSITORY and
            pins["version"] == version, "Candidate pin version/repository/schema mismatch")
    require(type(pins["sourceCommit"]) is str and re.fullmatch(r"[a-f0-9]{40}", pins["sourceCommit"]) is not None and
            pins["sourceCommit"] != "0" * 40, "Candidate source commit is not pinned")
    positive_int(pins["candidateRunId"], 2**63 - 1, "Candidate run ID")
    positive_int(pins["candidateArtifactId"], 2**63 - 1, "Candidate artifact ID")
    exact_keys(pins["candidateArchive"], {"bytes", "sha256"}, "candidate archive")
    positive_int(pins["candidateArchive"]["bytes"], 200 * MIB, "Candidate archive size")
    pinned_sha(pins["candidateArchive"]["sha256"], "Candidate archive")
    for key, filename, maximum in (("installer", f"SayWhat-{version}-Setup.exe", 160 * MIB),
            ("correspondingSource", "SayWhat-Corresponding-Source.zip", 64 * MIB),
            ("rawAppDll", "diagnostics/SayWhat.dll", 32 * MIB)):
        exact_keys(pins[key], {"file", "bytes", "sha256"}, key)
        require(pins[key]["file"] == filename, "Unexpected " + key + " filename")
        positive_int(pins[key]["bytes"], maximum, key + " size")
        pinned_sha(pins[key]["sha256"], key)
    pinned_sha(pins["releaseNotesSha256"], "Reviewed release notes")
    antivirus = pins["antivirus"]
    exact_keys(antivirus, {"candidateDetection", "vendorClearance", "review", "predecessorDetection"}, "antivirus evidence")
    require(antivirus["candidateDetection"] == "unknown" and antivirus["vendorClearance"] is False and
            antivirus["review"] == "submitted-pending", "This workflow requires the reviewed pending/unknown antivirus status")
    predecessor = antivirus["predecessorDetection"]
    exact_keys(predecessor, {"version", "product", "signature"}, "predecessor antivirus evidence")
    require(type(predecessor["version"]) is str and re.fullmatch(VERSION_PATTERN, predecessor["version"]) is not None and
            predecessor["version"] != version and predecessor["product"] == "Bitdefender" and
            type(predecessor["signature"]) is str and re.fullmatch(r"[A-Za-z0-9:._-]{1,128}", predecessor["signature"]) is not None,
            "Invalid predecessor antivirus evidence")
    return pins


class Candidate:
    def __init__(self, pins, version, notes):
        self.pins = validate_pins(pins, version)
        self.version, self.tag, self.source = version, "v" + version, pins["sourceCommit"]
        self.run, self.artifact = pins["candidateRunId"], pins["candidateArtifactId"]
        self.installer, self.source_file = pins["installer"]["file"], pins["correspondingSource"]["file"]
        self.complete, self.checksums = f"SayWhat-{version}-Complete.zip", f"SHA256SUMS-{version}.txt"
        self.package_top = {self.installer, self.source_file, "DISTRIBUTION-NOTES.md", "LICENSE", "README.md",
            "release-manifest.json", "SHA256SUMS.txt", "SOURCE-AND-LICENSES.md", "THIRD-PARTY-NOTICES.md"}
        require(type(notes) is bytes and 0 < len(notes) <= 64 * 1024 and sha(notes) == pins["releaseNotesSha256"],
                "Reviewed release notes SHA-256/size mismatch")
        text = notes.decode("utf-8")
        require("\0" not in text and text.strip(), "Invalid reviewed release notes")
        self.notes = text.rstrip() + f"\n\nSource commit: `{self.source}`\n\nCandidate run: https://github.com/{REPOSITORY}/actions/runs/{self.run}\n"
        self.title = "SayWhat? " + version + " preview"


def load_candidate(version):
    require(re.fullmatch(VERSION_PATTERN, version) is not None, "Invalid explicit version")
    path = ROOT / "eng" / "release-candidates" / (version + ".json")
    require(path.is_file() and not path.is_symlink() and 0 < path.stat().st_size <= 16 * 1024, "Invalid or oversized pin manifest")
    pins = validate_pins(strict_json(read_file(path, 16 * 1024)), version)
    notes = path.with_suffix(".md")
    require(notes.is_file() and not notes.is_symlink() and 0 < notes.stat().st_size <= 64 * 1024, "Invalid or oversized release notes")
    return Candidate(pins, version, read_file(notes, 64 * 1024))


def read_file(path, maximum):
    with path.open("rb") as stream:
        data = stream.read(maximum + 1)
    require(len(data) <= maximum, "File exceeds its reviewed size bound")
    return data


def safe_name(name):
    require(isinstance(name, str) and 0 < len(name) <= 1024 and "\\" not in name and ":" not in name and
            all(ord(character) >= 32 and ord(character) != 127 for character in name),
            "Unsafe archive path")
    parts = name.rstrip("/").split("/")
    require(not name.startswith("/") and all(part not in ("", ".", "..") for part in parts), "Unsafe archive path")
    require(all(not part.endswith((".", " ")) and
            re.fullmatch(r"(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?", part, re.IGNORECASE) is None for part in parts),
            "Unsafe Windows archive path")
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
        exact_keys(item, {"Path", "Bytes", "Sha256"}, "file inventory entry")
        name = safe_name(item["Path"])
        require(name not in result and type(item["Bytes"]) is int and item["Bytes"] >= 0 and
                re.fullmatch(r"[a-f0-9]{64}", item["Sha256"]) is not None, "Invalid manifest entry")
        result[name] = {"Bytes": item["Bytes"], "Sha256": item["Sha256"]}
    return result


class Github:
    def __init__(self, token, candidate):
        self.token = token
        self.candidate = candidate

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
            return status, strict_json(data) if data and status not in (302, 404) else None, location
        finally:
            if upload is not None and body is not None:
                body.close()
            connection.close()

    def api(self, method, suffix, payload=None, expected=(200,)):
        return self.request(method, f"/repos/{REPOSITORY}" + suffix, payload, expected)[1]

    def download_candidate(self, destination):
        pin = self.candidate.pins["candidateArchive"]
        _, _, location = self.request("GET", f"/repos/{REPOSITORY}/actions/artifacts/{self.candidate.artifact}/zip", expected=(302,))
        uri = urlsplit(location or "")
        require(uri.scheme == "https" and uri.port in (None, 443) and uri.username is None and uri.password is None and not uri.fragment and uri.hostname is not None and
                (uri.hostname.endswith(".blob.core.windows.net") or uri.hostname.endswith(".actions.githubusercontent.com") or
                 uri.hostname.endswith(".githubusercontent.com")), "Unexpected artifact storage redirect")
        # Do not forward the GitHub token to the signed artifact storage URL.
        connection = http.client.HTTPSConnection(uri.hostname, timeout=180)
        digest, count = hashlib.sha256(), 0
        try:
            connection.request("GET", uri.path + ("?" + uri.query if uri.query else ""))
            response = connection.getresponse()
            require(response.status == 200 and int(response.getheader("Content-Length", "0")) == pin["bytes"], "Candidate download size/status mismatch")
            with destination.open("xb") as target:
                while block := response.read(1024 * 1024):
                    count += len(block)
                    require(count <= pin["bytes"], "Oversized candidate download")
                    digest.update(block)
                    target.write(block)
        finally:
            connection.close()
        require(count == pin["bytes"] and digest.hexdigest() == pin["sha256"], "Candidate ZIP SHA-256 mismatch")


def archive_read(archive, name, maximum):
    require(archive.getinfo(name).file_size <= maximum, "Oversized " + name)
    return archive.read(name)


def verify_candidate(path, output, candidate):
    c, pins = candidate, candidate.pins
    require(path.stat().st_size == pins["candidateArchive"]["bytes"] and file_sha(path) == pins["candidateArchive"]["sha256"],
            "Candidate archive differs from its immutable pins")
    with zipfile.ZipFile(path) as archive:
        inventory = zip_inventory(archive)
        require("candidate-manifest.json" in inventory and inventory["candidate-manifest.json"]["Bytes"] <= 512 * 1024,
                "Missing or oversized candidate manifest")
        manifest = strict_json(archive_read(archive, "candidate-manifest.json", 512 * 1024))
        require(type(manifest["SchemaVersion"]) is int and manifest["SchemaVersion"] == 1 and manifest["App"] == "SayWhat?" and manifest["Version"] == c.version and
                manifest["SourceCommit"] == c.source and manifest["Status"] == "unreleased-review-candidate" and
                manifest["Published"] is False and manifest["ModelsBundled"] is False and manifest["UserDataIncluded"] is False,
                "Candidate provenance mismatch")
        expected = manifest_entries(manifest["Files"])
        require(set(inventory) == set(expected) | {"candidate-manifest.json", "SHA256SUMS.txt"}, "Candidate contains missing or extra files")
        require({name for name in inventory if not name.startswith("package/")} ==
                {"CANDIDATE-README.txt", "candidate-manifest.json", "SHA256SUMS.txt", "diagnostics/SayWhat.dll"},
                "Unexpected non-package candidate contents")
        require(all(inventory[name] == value for name, value in expected.items()), "Candidate file inventory mismatch")
        require(checksum_entries(archive_read(archive, "SHA256SUMS.txt", 8 * MIB).decode("utf-8-sig")) ==
                {name: item["Sha256"] for name, item in inventory.items() if name != "SHA256SUMS.txt"}, "Candidate checksum inventory mismatch")
        require(manifest["InstallerSha256"] == pins["installer"]["sha256"] and manifest["CorrespondingSourceSha256"] == pins["correspondingSource"]["sha256"] and
                manifest["RawAppDllSha256"] == pins["rawAppDll"]["sha256"] and inventory["diagnostics/SayWhat.dll"] ==
                {"Bytes": pins["rawAppDll"]["bytes"], "Sha256": pins["rawAppDll"]["sha256"]},
                "Pinned binary/source identity mismatch")
        package = {name[len("package/"):]: value for name, value in inventory.items() if name.startswith("package/")}
        require({name for name in package if "/" not in name} == c.package_top and
                all("/" not in name or name.startswith("licenses/") for name in package), "Unexpected package contents")
        require(package[c.installer] == {"Bytes": pins["installer"]["bytes"], "Sha256": pins["installer"]["sha256"]} and
                package[c.source_file] == {"Bytes": pins["correspondingSource"]["bytes"], "Sha256": pins["correspondingSource"]["sha256"]}, "Pinned installer/source sizes differ")
        require(checksum_entries(archive_read(archive, "package/SHA256SUMS.txt", 8 * MIB).decode("utf-8-sig")) ==
                {name: item["Sha256"] for name, item in package.items() if name != "SHA256SUMS.txt"}, "Package checksum inventory mismatch")
        released = strict_json(archive_read(archive, "package/release-manifest.json", 2 * MIB))
        require(type(released["SchemaVersion"]) is int and released["SchemaVersion"] == 1 and released["App"] == "SayWhat?" and
                released["Published"] is False and released["ModelsBundled"] is False and released["UserDataIncluded"] is False and
                released["ReleaseStatus"] == "prepared-preview-not-certified" and released["MaintainerGrantConfirmed"] is True and
                released["Version"] == c.version and released["Installer"]["File"] == c.installer and
                released["Installer"]["Bytes"] == pins["installer"]["bytes"] and released["Installer"]["Sha256"] == pins["installer"]["sha256"] and
                released["CorrespondingSource"]["File"] == c.source_file and released["CorrespondingSource"]["Bytes"] == pins["correspondingSource"]["bytes"] and
                released["CorrespondingSource"]["Sha256"] == pins["correspondingSource"]["sha256"] and released["OriginalContributionLicense"] == "GPL-3.0-only",
                "Exported binary/source/license pair differs")
        require(manifest_entries(released["Files"]) == {name: item for name, item in package.items()
                if name not in ("release-manifest.json", "SHA256SUMS.txt")}, "Export file inventory mismatch")
        with zipfile.ZipFile(io.BytesIO(archive_read(archive, "package/" + c.source_file, 64 * MIB))) as source:
            require(zip_inventory(source) == manifest_entries(released["CorrespondingSource"]["Files"]), "Corresponding source inventory mismatch")
        output.mkdir()
        for name in package:
            target = output.joinpath(*PurePosixPath(name).parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.open("package/" + name) as stream, target.open("xb") as written:
                while block := stream.read(1024 * 1024):
                    written.write(block)
    # A complete distribution is assembled only from unchanged package files.
    complete = output.parent / c.complete
    with zipfile.ZipFile(complete, "x", compression=zipfile.ZIP_STORED) as distribution:
        for name in sorted(package):
            info = zipfile.ZipInfo(name, date_time=(2000, 1, 1, 0, 0, 0))
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            with output.joinpath(*PurePosixPath(name).parts).open("rb") as source, distribution.open(info, "w") as target:
                while block := source.read(1024 * 1024):
                    target.write(block)
    with zipfile.ZipFile(complete) as distribution:
        require(zip_inventory(distribution) == package, "Complete ZIP differs from unchanged package")
    assets = [output / name for name in sorted(c.package_top)] + [complete]
    checksum = output.parent / c.checksums
    checksum.write_text("".join(file_sha(file) + "  " + file.name + "\n" for file in assets), encoding="utf-8")
    return assets + [checksum]


def file_sha(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(1024 * 1024):
            digest.update(block)
    return digest.hexdigest()


def verify_draft(release, candidate, release_id=None):
    require(type(release) is dict and release["draft"] is True and release["prerelease"] is True and
            release["tag_name"] == candidate.tag and release["target_commitish"] == candidate.source and
            release["name"] == candidate.title and release["body"] == candidate.notes and
            (release_id is None or release["id"] == release_id), "Existing release is not this unchanged promotion draft")


def main(candidate):
    c, pins = candidate, candidate.pins
    require(os.environ.get("GITHUB_ACTIONS") == "true" and os.environ.get("GITHUB_REPOSITORY") == REPOSITORY and
            os.environ.get("GITHUB_REF") == "refs/heads/main" and
            os.environ.get("GITHUB_EVENT_NAME") == "workflow_dispatch" and os.environ.get("SAYWHAT_ACKNOWLEDGE_REVIEW_PENDING") == "true",
            "Promotion requires the acknowledged manual workflow in the project repository")
    token = os.environ.get("GH_TOKEN", "")
    require(bool(token), "Missing workflow token")
    github = Github(token, c)
    run = github.api("GET", f"/actions/runs/{c.run}")
    require(run["id"] == c.run and run["head_sha"] == c.source and run["status"] == "completed" and run["conclusion"] == "success" and
            run["head_branch"] == "main" and run["event"] == "workflow_dispatch" and run["head_repository"]["full_name"] == REPOSITORY and
            run["path"] == ".github/workflows/release-candidate.yml", "Candidate workflow provenance mismatch")
    artifact = github.api("GET", f"/actions/artifacts/{c.artifact}")
    require(artifact["id"] == c.artifact and artifact["expired"] is False and artifact["size_in_bytes"] == pins["candidateArchive"]["bytes"] and
            artifact["workflow_run"]["id"] == c.run and artifact["workflow_run"]["head_sha"] == c.source and
            artifact.get("digest") == "sha256:" + pins["candidateArchive"]["sha256"] and
            re.fullmatch(r"SayWhat-unreleased-AV-review-" + c.source + "-" + str(c.run) + r"-[1-9][0-9]{0,3}", artifact["name"]) is not None,
            "Pinned artifact metadata mismatch")
    output = ROOT / "artifacts" / ("promote-" + c.version)
    require(not output.is_symlink() and not output.parent.is_symlink(), "Redirected promotion output")
    output.mkdir(parents=True, exist_ok=False)
    archive = output / "verified-candidate.zip"
    github.download_candidate(archive)
    assets = verify_candidate(archive, output / "package", c)
    expected = {file.name: {"name": file.name, "sizeBytes": file.stat().st_size, "sha256": file_sha(file)} for file in assets}
    require(len(expected) == 11, "Unexpected release asset set")

    def verify_tag():
        reference = github.api("GET", "/git/ref/tags/" + c.tag, expected=(200, 404))
        if reference is None or "object" not in reference:
            github.api("POST", "/git/refs", {"ref": "refs/tags/" + c.tag, "sha": c.source}, expected=(201,))
            reference = github.api("GET", "/git/ref/tags/" + c.tag)
        target = reference["object"]
        for _ in range(3):
            if target["type"] != "tag":
                break
            target = github.api("GET", "/git/tags/" + target["sha"])["object"]
        require(target["type"] == "commit" and target["sha"] == c.source, "Release tag does not target the verified source commit")

    verify_tag()
    release = github.api("GET", "/releases/tags/" + c.tag, expected=(200, 404))
    if release is None or "id" not in release:
        release = github.api("POST", "/releases", {"tag_name": c.tag, "target_commitish": c.source, "name": c.title,
            "body": c.notes, "draft": True, "prerelease": True, "make_latest": "false"}, expected=(201,))
    verify_draft(release, c)
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
    # Another maintainer can edit draft metadata while uploads are running.
    # Revalidate the exact draft, body and source immediately before publication.
    verify_draft(github.api("GET", f"/releases/{release_id}"), c, release_id)
    release = github.api("PATCH", f"/releases/{release_id}", {"draft": False, "prerelease": True, "make_latest": "false"})
    require(release["draft"] is False and release["prerelease"] is True and release["published_at"] and
            release["id"] == release_id and release["tag_name"] == c.tag and release["target_commitish"] == c.source and
            release["name"] == c.title and release["body"] == c.notes, "Publication identity or status mismatch")
    current = uploaded_assets()
    require(set(current) == set(expected) and all(current[name]["size"] == item["sizeBytes"] and
        current[name].get("digest") == "sha256:" + item["sha256"] for name, item in expected.items()), "Post-publication asset mismatch")
    receipt = {"schemaVersion": 1, "version": c.version, "sourceCommit": c.source, "candidateRunId": c.run, "candidateArtifactId": c.artifact,
        "candidateArchiveSha256": pins["candidateArchive"]["sha256"], "tag": c.tag, "publishedAt": release["published_at"], "releaseURL": release["html_url"],
        "knownAVStatus": pins["antivirus"], "reviewedReleaseNotesSha256": pins["releaseNotesSha256"], "rawAppDll": pins["rawAppDll"],
        "installer": expected[c.installer], "source": expected[c.source_file], "distribution": expected[c.complete],
        "assets": [dict(expected[name], id=current[name]["id"], digest=current[name]["digest"], url=current[name]["browser_download_url"]) for name in sorted(expected)]}
    (output / "promotion-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print("Verified and published " + release["html_url"])


def full_candidate_fixture_test(version):
    def encoded(value):
        return json.dumps(value, sort_keys=True, separators=(",", ":")).encode("utf-8")

    def inventory(files):
        return [{"Path": name, "Bytes": len(data), "Sha256": sha(data)} for name, data in sorted(files.items())]

    def checksums(files):
        return "".join(sha(data) + "  " + name + "\n" for name, data in sorted(files.items())).encode("utf-8")

    def zipped(files):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_STORED) as archive:
            for name, data in sorted(files.items()):
                archive.writestr(name, data)
        return buffer.getvalue()

    def make_candidate(mode="valid"):
        notes = b"Synthetic reviewed release notes; antivirus status is unknown.\n"
        installer_name = "SayWhat-" + version + "-Setup.exe"
        source_name = "SayWhat-Corresponding-Source.zip"
        installer = b"synthetic installer fixture; inert ASCII, not an executable\n"
        raw_dll = b"synthetic DLL fixture; inert ASCII, not a PE image\n"
        source_files = {"LICENSE": b"synthetic GPL-3.0-only fixture\n", "LocalTalk/Fixture.cs": b"// synthetic source, never compiled\n"}
        source_bytes = zipped(source_files)
        source_inventory = inventory(source_files)
        if mode == "bad-source-inventory":
            source_inventory[0]["Sha256"] = "f" * 64
        package = {
            installer_name: installer, source_name: source_bytes, "LICENSE": b"synthetic GPL-3.0-only license\n",
            "DISTRIBUTION-NOTES.md": b"synthetic distribution notes\n", "README.md": b"synthetic recipient README\n",
            "SOURCE-AND-LICENSES.md": b"synthetic source notice\n", "THIRD-PARTY-NOTICES.md": b"synthetic notices\n",
            "licenses/fixture.txt": b"synthetic dependency license\n"
        }
        release = {
            "SchemaVersion": 1, "App": "SayWhat?", "Version": version,
            "Published": False, "ModelsBundled": False, "UserDataIncluded": False,
            "ReleaseStatus": "prepared-preview-not-certified", "MaintainerGrantConfirmed": True,
            "OriginalContributionLicense": "MIT" if mode == "bad-gpl" else "GPL-3.0-only",
            "Installer": {"File": installer_name, "Bytes": len(installer), "Sha256": sha(installer)},
            "CorrespondingSource": {"File": source_name, "Bytes": len(source_bytes), "Sha256": sha(source_bytes), "Files": source_inventory},
            "Files": inventory(package)
        }
        package["release-manifest.json"] = encoded(release)
        package["SHA256SUMS.txt"] = checksums(package)
        outer = {"package/" + name: data for name, data in package.items()}
        outer.update({"CANDIDATE-README.txt": b"synthetic candidate notice\n", "diagnostics/SayWhat.dll": raw_dll})
        outer["candidate-manifest.json"] = encoded({
            "SchemaVersion": 1, "App": "SayWhat?", "Version": version, "SourceCommit": "a" * 40,
            "Status": "unreleased-review-candidate", "Published": False, "ModelsBundled": False, "UserDataIncluded": False,
            "InstallerSha256": sha(installer), "CorrespondingSourceSha256": sha(source_bytes), "RawAppDllSha256": sha(raw_dll),
            "Files": inventory(outer)
        })
        outer["SHA256SUMS.txt"] = checksums(outer)
        archive_bytes = zipped(outer)
        pins = {
            "schemaVersion": 1, "repository": "z2six/saywhat", "version": version, "sourceCommit": "a" * 40,
            "candidateRunId": 1, "candidateArtifactId": 2,
            "candidateArchive": {"bytes": len(archive_bytes), "sha256": sha(archive_bytes)},
            "installer": {"file": installer_name, "bytes": len(installer), "sha256": sha(installer)},
            "correspondingSource": {"file": source_name, "bytes": len(source_bytes), "sha256": sha(source_bytes)},
            "rawAppDll": {"file": "diagnostics/SayWhat.dll", "bytes": len(raw_dll), "sha256": sha(raw_dll)},
            "releaseNotesSha256": sha(notes),
            "antivirus": {"candidateDetection": "unknown", "vendorClearance": False, "review": "submitted-pending",
                "predecessorDetection": {"version": "0.0.0" if version != "0.0.0" else "0.0.1", "product": "Bitdefender", "signature": "Fixture.Signature"}}
        }
        return Candidate(pins, version, notes), archive_bytes, package

    fixture_base = ROOT / "artifacts" / "test-results"
    fixture_base.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="saywhat-promotion-synthetic-", dir=fixture_base) as directory:
        root = Path(directory).resolve()
        require(root.parent == fixture_base.resolve() and root.name.startswith("saywhat-promotion-synthetic-"),
                "Unexpected synthetic fixture temporary directory")
        candidate, contents, package = make_candidate()
        archive = root / "candidate.zip"
        archive.write_bytes(contents)
        assets = verify_candidate(archive, root / "package", candidate)
        require(len(assets) == 11 and len({path.name for path in assets}) == 11, "Synthetic complete candidate asset count differs")
        with zipfile.ZipFile(root / candidate.complete) as complete:
            require(zip_inventory(complete) == {item["Path"]: {"Bytes": item["Bytes"], "Sha256": item["Sha256"]} for item in inventory(package)},
                    "Complete archive is not exactly the unchanged package, including nested license files")
            require(all(not name.startswith("diagnostics/") and name != "CANDIDATE-README.txt" for name in complete.namelist()),
                    "Candidate diagnostics leaked into Complete distribution")
        exported = {path.name: file_sha(path) for path in assets if path.name != candidate.checksums}
        require(checksum_entries((root / candidate.checksums).read_text(encoding="utf-8")) == exported,
                "Generated release checksums do not identify the exact ten recipient assets")
        for mode in ("bad-source-inventory", "bad-gpl"):
            rejected, invalid_bytes, _ = make_candidate(mode)
            invalid_archive = root / (mode + ".zip")
            invalid_archive.write_bytes(invalid_bytes)
            try:
                verify_candidate(invalid_archive, root / mode, rejected)
            except RuntimeError:
                require(not (root / mode).exists(), "Invalid candidate created recipient output before verification")
            else:
                raise RuntimeError("Candidate failed to reject " + mode)
    return 6


def self_test(version):
    checks = 0
    notes = b"Synthetic reviewed release notes. No release or executable.\n"
    pins = {"schemaVersion": 1, "repository": REPOSITORY, "version": version, "sourceCommit": "a" * 40,
        "candidateRunId": 1, "candidateArtifactId": 2, "candidateArchive": {"bytes": 1000, "sha256": "b" * 64},
        "installer": {"file": f"SayWhat-{version}-Setup.exe", "bytes": 1, "sha256": "c" * 64},
        "correspondingSource": {"file": "SayWhat-Corresponding-Source.zip", "bytes": 2, "sha256": "d" * 64},
        "rawAppDll": {"file": "diagnostics/SayWhat.dll", "bytes": 3, "sha256": "e" * 64},
        "releaseNotesSha256": sha(notes), "antivirus": {"candidateDetection": "unknown", "vendorClearance": False,
            "review": "submitted-pending", "predecessorDetection": {"version": "0.2.11" if version != "0.2.11" else "0.2.10",
                "product": "Bitdefender", "signature": "Gen:Fixture"}}}
    validate_pins(pins, version)
    candidate = Candidate(pins, version, notes)
    checks += 1
    cases = [("sourceCommit", None), ("sourceCommit", "0" * 40), ("candidateRunId", None), ("candidateRunId", True),
        ("candidateArtifactId", 0), ("candidateArtifactId", 2**63), ("releaseNotesSha256", None),
        ("releaseNotesSha256", "0" * 64), ("version", "../" + version), ("schemaVersion", True)]
    for field, value in cases:
        broken = copy.deepcopy(pins)
        broken[field] = value
        try:
            validate_pins(broken, version)
        except RuntimeError:
            checks += 1
        else:
            raise RuntimeError("Incomplete or invalid pin accepted: " + field)
    for field in ("candidateArchive", "installer", "correspondingSource", "rawAppDll"):
        for key, value in (("bytes", None), ("bytes", False), ("bytes", -1), ("bytes", 201 * MIB),
                           ("sha256", None), ("sha256", "pending"), ("sha256", "0" * 64), ("unexpected", True)):
            broken = copy.deepcopy(pins)
            broken[field][key] = value
            try:
                validate_pins(broken, version)
            except RuntimeError:
                checks += 1
            else:
                raise RuntimeError("Invalid nested pin accepted: " + field + "." + key)
    for text in ('{"a":1,"a":2}', '{"nested":{"a":1,"a":2}}', '{"a":NaN}', '{"a":Infinity}'):
        try:
            strict_json(text)
        except RuntimeError:
            checks += 1
        else:
            raise RuntimeError("Ambiguous JSON accepted")
    broken = copy.deepcopy(pins)
    broken["unexpected"] = "field"
    try:
        validate_pins(broken, version)
    except RuntimeError:
        checks += 1
    else:
        raise RuntimeError("Unknown pin field accepted")
    for changed_notes in (notes + b"unreviewed edit", b"", b"x" * (64 * 1024 + 1)):
        try:
            Candidate(pins, version, changed_notes)
        except RuntimeError:
            checks += 1
        else:
            raise RuntimeError("Unreviewed or unbounded notes accepted")
    draft = {"id": 7, "draft": True, "prerelease": True, "tag_name": candidate.tag,
        "target_commitish": candidate.source, "name": candidate.title, "body": candidate.notes}
    verify_draft(draft, candidate, 7)
    checks += 1
    for field, value in (("id", 8), ("draft", False), ("prerelease", False), ("tag_name", "other"),
                         ("target_commitish", "f" * 40), ("name", "other"), ("body", "unreviewed")):
        changed = dict(draft, **{field: value})
        try:
            verify_draft(changed, candidate, 7)
        except RuntimeError:
            checks += 1
        else:
            raise RuntimeError("Changed draft accepted before publication")
    for name in ("package/LICENSE", "licenses/example.txt", ".github/workflows/ci.yml"):
        safe_name(name)
        checks += 1
    for name in ("../secret", "/absolute", "C:/private", "package\\file", "package//file", "x/./file", "x/../file", "nul\0file",
                 "package/line\nbreak", "x/tab\tfile", "x/trailing.", "x/trailing ", "x/CON", "x/nul.txt", "x/COM1.log"):
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
    checks += full_candidate_fixture_test(version)
    print(f"PASS {checks} promotion validation checks; no network or executable content used")


if __name__ == "__main__":
    try:
        parser = argparse.ArgumentParser(description=__doc__)
        parser.add_argument("--version", required=True, help="Exact reviewed version in eng/release-candidates")
        mode = parser.add_mutually_exclusive_group()
        mode.add_argument("--self-test", action="store_true", help="Run synthetic validation fixtures without network")
        mode.add_argument("--validate-only", action="store_true", help="Validate all immutable pins and reviewed notes without network")
        arguments = parser.parse_args()
        require(re.fullmatch(VERSION_PATTERN, arguments.version) is not None, "Invalid explicit version")
        if arguments.self_test:
            self_test(arguments.version)
        else:
            candidate = load_candidate(arguments.version)
            if arguments.validate_only:
                print("Validated complete candidate pins and reviewed notes; no network, output or publication")
            else:
                main(candidate)
    except Exception as error:
        print(type(error).__name__ + ": " + str(error), file=sys.stderr)
        sys.exit(1)
