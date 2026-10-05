(() => {
  "use strict";
  const byId = (id) => document.getElementById(id);
  const status = byId("release-status");
  const installerLink = byId("installer-link");
  const refresh = byId("refresh-release");
  const gatedLinks = [installerLink, byId("release-notes-link"), byId("source-link"), byId("distribution-link"), byId("repository-link")];
  const repository = "https://github.com/z2six/saywhat";
  const metadataLimit = 32 * 1024;
  let checksum = "";

  function githubUrl(value, asset = false) {
    if (typeof value !== "string") return null;
    try {
      const url = new URL(value);
      if (url.protocol !== "https:" || url.hostname !== "github.com" || url.port || url.username || url.password || url.search || url.hash) return null;
      if (url.pathname !== "/z2six/saywhat" && !url.pathname.startsWith("/z2six/saywhat/")) return null;
      if (asset && !/^\/z2six\/saywhat\/releases\/download\/[^/]+\/[^/]+$/.test(url.pathname)) return null;
      return url.href;
    } catch { return null; }
  }

  function disableDownloads(message) {
    gatedLinks.forEach((link) => { link.href = "#release-status"; link.setAttribute("aria-disabled", "true"); link.removeAttribute("download"); });
    installerLink.classList.add("pending");
    installerLink.textContent = "Installer being prepared ↓";
    status.textContent = message;
    byId("release-meta").textContent = "Preview · Windows 11 x64 · Unsigned installer";
    byId("checksum-panel").hidden = true;
    checksum = "";
  }

  function applyRelease(manifest) {
    const installer = manifest && manifest.installer;
    const urls = manifest && {
      installer: githubUrl(installer && installer.url, true),
      notes: githubUrl(manifest.releaseUrl),
      source: githubUrl(manifest.source && manifest.source.url, true),
      distribution: githubUrl(manifest.distribution && manifest.distribution.url, true),
      repository: githubUrl(manifest.repositoryUrl)
    };
    const version = manifest && typeof manifest.version === "string" ? manifest.version : "";
    const assetBase = `${repository}/releases/download/v${version}/`;
    const matchingZip = (url) => url && url.startsWith(assetBase) && /^[A-Za-z0-9_.+-]+\.zip$/.test(url.slice(assetBase.length));
    if (!manifest || manifest.schemaVersion !== 1 || manifest.ready !== true || manifest.channel !== "preview" ||
        version.length > 64 || !/^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$/.test(version) ||
        manifest.sourceLicense !== "GPL-3.0-only" || !installer || !/^[a-fA-F0-9]{64}$/.test(installer.sha256 || "") ||
        !manifest.source || !/^[a-fA-F0-9]{64}$/.test(manifest.source.sha256 || "") ||
        !manifest.distribution || !/^[a-fA-F0-9]{64}$/.test(manifest.distribution.sha256 || "") ||
        !Number.isSafeInteger(installer.sizeBytes) || installer.sizeBytes <= 0 ||
        !urls || Object.values(urls).some((url) => !url) || urls.repository !== repository ||
        urls.notes !== `${repository}/releases/tag/v${version}` ||
        urls.installer !== `${assetBase}SayWhat-${version}-Setup.exe` ||
        !matchingZip(urls.source) || !matchingZip(urls.distribution)) {
      disableDownloads("The public installer is being prepared. Downloads are not available yet.");
      return;
    }
    const byteSize = (installer.sizeBytes / 1048576).toFixed(0);
    installerLink.href = urls.installer;
    installerLink.removeAttribute("aria-disabled");
    installerLink.classList.remove("pending");
    installerLink.textContent = "Download for Windows ↓";
    byId("release-notes-link").href = urls.notes;
    byId("source-link").href = urls.source;
    byId("distribution-link").href = urls.distribution;
    byId("repository-link").href = urls.repository;
    gatedLinks.forEach((link) => { link.removeAttribute("aria-disabled"); });
    status.textContent = `Preview ${manifest.version} is available. Review the requirements before installing.`;
    byId("release-meta").textContent = `Version ${manifest.version} · ${byteSize} MiB · Windows 11 x64 · Unsigned installer`;
    checksum = installer.sha256.toLowerCase();
    byId("installer-sha").textContent = checksum;
    byId("checksum-panel").hidden = false;
  }

  async function readMetadata(response, signal) {
    const declared = response.headers.get("Content-Length");
    if (declared && /^\d+$/.test(declared) && Number(declared) > metadataLimit)
      throw new Error("Release details exceed the metadata size limit");
    if (!response.body) throw new Error("Release details have no response body");
    const reader = response.body.getReader();
    const chunks = [];
    let total = 0;
    try {
      while (true) {
        if (signal.aborted) throw new Error("Release details request expired");
        const { done, value } = await reader.read();
        if (done) break;
        total += value.byteLength;
        if (total > metadataLimit) {
          reader.cancel().catch(() => {});
          throw new Error("Release details exceed the metadata size limit");
        }
        if (value.byteLength) chunks.push(value);
      }
    } finally { reader.releaseLock(); }
    const bytes = new Uint8Array(total);
    let offset = 0;
    chunks.forEach((chunk) => { bytes.set(chunk, offset); offset += chunk.byteLength; });
    return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
  }

  async function loadRelease() {
    refresh.disabled = true;
    refresh.textContent = "Checking download status…";
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 5000);
    try {
      const response = await fetch("release.json", { cache: "no-store", credentials: "omit", signal: controller.signal });
      if (!response.ok) throw new Error("Release details unavailable");
      applyRelease(await readMetadata(response, controller.signal));
    } catch {
      controller.abort();
      disableDownloads("Download status could not be checked. Try refreshing; no installer link has been enabled.");
    } finally {
      clearTimeout(timeout);
      refresh.disabled = false;
      refresh.textContent = "Refresh download status";
    }
  }

  gatedLinks.forEach((link) => link.addEventListener("click", (event) => {
    if (link.getAttribute("aria-disabled") === "true") { event.preventDefault(); status.focus(); }
  }));
  refresh.addEventListener("click", loadRelease);
  byId("copy-checksum").addEventListener("click", async () => {
    if (!checksum) return;
    try { await navigator.clipboard.writeText(checksum); byId("copy-status").textContent = "Checksum copied."; }
    catch { byId("copy-status").textContent = "Could not copy. Select and copy the checksum above."; }
  });
  loadRelease();
})();
