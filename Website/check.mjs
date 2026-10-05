// Offline structural and release-gate checks. No web server, network, app,
// installer, model, audio device or real clipboard is accessed.
import assert from "node:assert/strict";
import { readFileSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import vm from "node:vm";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "public");
const html = readFileSync(resolve(root, "index.html"), "utf8");
const script = readFileSync(resolve(root, "release.js"), "utf8");
const manifest = JSON.parse(readFileSync(resolve(root, "release.json"), "utf8"));
let checks = 0;
function check(condition, message) { checks++; assert.ok(condition, message); }
check(manifest.schemaVersion === 1 && manifest.ready === false, "Scaffold must not advertise unpublished downloads.");
check(manifest.channel === "preview", "Preview status is explicit.");
check((html.match(/<main\b/g) || []).length === 1 && (html.match(/<\/main>/g) || []).length === 1, "Exactly one main landmark exists.");
check((html.match(/<h1\b/g) || []).length === 1, "One clear page heading exists.");
check(html.includes('lang="en"') && html.includes('class="skip-link"'), "English page and keyboard skip link exist.");
check(!/<style\b|<script\b[^>]*>\s*[^<\s]/.test(html), "No inline code bypasses the self-only content policy.");
const ids = Array.from(html.matchAll(/\bid="([^"]+)"/g), (match) => match[1]);
check(new Set(ids).size === ids.length, "Element IDs are unique.");
for (const match of html.matchAll(/\b(?:href|src)="([^"]+)"/g)) {
  const value = match[1];
  if (value === "#" || value.startsWith("https://")) continue;
  if (value.startsWith("#")) check(ids.includes(value.slice(1)), `Anchor ${value} resolves.`);
  else check(existsSync(resolve(root, value.replace(/^\//, ""))), `Local asset ${value} exists.`);
}
const headers = readFileSync(resolve(root, "_headers"), "utf8");
check(headers.includes("microphone=()") && headers.includes("script-src 'self'") && headers.includes("Cache-Control: no-store"), "Permissions and manifest-cache policy remain conservative.");
new vm.Script(script, { filename: "release.js" });
checks++;

class Element {
  constructor() {
    this.href = "#release-status";
    this.textContent = "";
    this.disabled = false;
    this.hidden = true;
    this.attributes = new Map([["aria-disabled", "true"]]);
    this.events = new Map();
    this.classes = new Set(["pending"]);
    this.classList = { add: (value) => this.classes.add(value), remove: (value) => this.classes.delete(value) };
  }
  setAttribute(name, value) { this.attributes.set(name, value); }
  removeAttribute(name) { this.attributes.delete(name); }
  getAttribute(name) { return this.attributes.get(name) ?? null; }
  addEventListener(name, callback) { this.events.set(name, callback); }
  focus() { this.focused = true; }
}
async function browserFixture(value, reject = false, transport = {}) {
  const elements = new Map(ids.map((id) => [id, new Element()]));
  const calls = [];
  const stats = { reads: 0, cancelled: false, released: false, timeoutCleared: false };
  const raw = transport.raw === undefined ? JSON.stringify(value) : transport.raw;
  const chunks = transport.chunks || [new TextEncoder().encode(raw)];
  let index = 0;
  let timeoutCallback;
  let copied = "";
  vm.runInNewContext(script, {
    document: { getElementById: (id) => { check(elements.has(id), `Script target ${id} exists.`); return elements.get(id); } },
    URL, AbortController, TextDecoder,
    setTimeout: (callback, milliseconds) => { check(milliseconds === 5000, "Request and response body share a five-second timeout."); timeoutCallback = callback; return 1; },
    clearTimeout: () => { stats.timeoutCleared = true; },
    navigator: { clipboard: { writeText: async (text) => { copied = text; } } },
    fetch: async (url, options) => {
      calls.push({ url, options });
      if (reject) throw new Error("Synthetic network failure");
      return {
        ok: transport.ok !== false,
        headers: { get: () => transport.contentLength ?? null },
        body: transport.noBody ? null : { getReader: () => ({
          read: async () => {
            stats.reads++;
            if (transport.stall) return await new Promise((_, rejected) => {
              options.signal.addEventListener("abort", () => rejected(new Error("Synthetic body read aborted")), { once: true });
            });
            return index < chunks.length ? { done: false, value: chunks[index++] } : { done: true };
          },
          cancel: async () => { stats.cancelled = true; },
          releaseLock: () => { stats.released = true; }
        }) }
      };
    }
  }, { filename: "release.js" });
  await new Promise(setImmediate);
  if (transport.stall) { timeoutCallback(); await new Promise(setImmediate); }
  check(calls.length === 1 && calls[0].url === "release.json", "Only one same-origin manifest read occurs.");
  check(calls[0].options.cache === "no-store" && calls[0].options.credentials === "omit", "Manifest read is uncached and excludes credentials.");
  check(!elements.get("refresh-release").disabled, "Status refresh re-enables after completion.");
  check(stats.timeoutCleared, "Every completion clears the request timer.");
  return { elements, calls, stats, copied: () => copied };
}
function assertUnavailable(fixture) {
  for (const id of ["installer-link", "source-link", "distribution-link", "release-notes-link", "repository-link"]) {
    check(fixture.elements.get(id).getAttribute("aria-disabled") === "true", `${id} remains disabled.`);
    check(fixture.elements.get(id).href === "#release-status", `${id} does not expose an unavailable asset URL.`);
  }
  check(fixture.elements.get("checksum-panel").hidden, "Checksum panel is hidden without a verified-ready manifest.");
}
const pending = await browserFixture(manifest);
assertUnavailable(pending);
let prevented = false;
pending.elements.get("installer-link").events.get("click")({ preventDefault: () => { prevented = true; } });
check(prevented && pending.elements.get("release-status").focused, "Pending download sends keyboard focus to honest status without navigating.");
const failed = await browserFixture(null, true);
assertUnavailable(failed);
check(failed.elements.get("release-status").textContent.includes("could not be checked"), "A manifest failure is shown, not claimed successful.");

// Entirely synthetic data: none of these future paths is requested or certified.
const ready = {
  schemaVersion: 1, ready: true, channel: "preview", version: "0.1.14",
  repositoryUrl: "https://github.com/z2six/saywhat",
  releaseUrl: "https://github.com/z2six/saywhat/releases/tag/v0.1.14",
  sourceLicense: "GPL-3.0-only",
  installer: { url: "https://github.com/z2six/saywhat/releases/download/v0.1.14/SayWhat-0.1.14-Setup.exe", sha256: "a".repeat(64), sizeBytes: 1048576 },
  source: { url: "https://github.com/z2six/saywhat/releases/download/v0.1.14/synthetic-source.zip", sha256: "b".repeat(64) },
  distribution: { url: "https://github.com/z2six/saywhat/releases/download/v0.1.14/synthetic-distribution.zip", sha256: "c".repeat(64) }
};
const available = await browserFixture(ready);
check(available.elements.get("installer-link").href === ready.installer.url, "A complete synthetic release binds the exact installer URL.");
check(available.elements.get("source-link").href === ready.source.url, "Source uses the shared typed manifest contract.");
check(available.elements.get("distribution-link").href === ready.distribution.url, "Complete ZIP alternative uses its actual manifest path.");
check(available.elements.get("release-notes-link").href === ready.releaseUrl, "Release notes use the app's shared releaseUrl field.");
check(available.elements.get("installer-link").getAttribute("aria-disabled") === null, "Ready installer becomes available.");
check(available.elements.get("release-meta").textContent.includes("0.1.14") && available.elements.get("release-meta").textContent.includes("1 MiB"), "Version and measured asset size are displayed.");
check(!available.elements.get("checksum-panel").hidden && available.elements.get("installer-sha").textContent === ready.installer.sha256, "Checksum is shown only with the ready release.");
await available.elements.get("copy-checksum").events.get("click")();
check(available.copied() === ready.installer.sha256, "Copy uses a fake clipboard and copies only the exact checksum.");
await available.elements.get("refresh-release").events.get("click")();
check(available.calls.length === 2, "Refresh performs one explicit additional manifest read, not background polling.");

for (const mutate of [
  (value) => { value.ready = false; },
  (value) => { value.schemaVersion = 2; },
  (value) => { value.channel = "stable"; },
  (value) => { value.version = "not-a-version"; },
  (value) => { value.sourceLicense = null; },
  (value) => { value.installer.sha256 = "invalid"; },
  (value) => { value.source.sha256 = null; },
  (value) => { value.distribution.sha256 = null; },
  (value) => { value.installer.sizeBytes = 0; },
  (value) => { value.installer.url = "https://other.example/installer.exe"; },
  (value) => { value.installer.url = "http://github.com/z2six/saywhat/releases/download/v0.1.14/installer.exe"; },
  (value) => { value.installer.url = "https://user:pass@github.com/z2six/saywhat/releases/download/v0.1.14/installer.exe"; },
  (value) => { value.installer.url = "https://github.com/other/project/releases/download/v0.1.14/installer.exe"; },
  (value) => { value.source.url = null; },
  (value) => { value.installer.url = "https://github.com/z2six/saywhat/releases/download/v0.1.13/SayWhat-0.1.14-Setup.exe"; },
  (value) => { value.installer.url = "https://github.com/z2six/saywhat/releases/download/v0.1.14/Other-Setup.exe"; },
  (value) => { value.source.url = "https://github.com/z2six/saywhat/releases/download/v0.1.13/synthetic-source.zip"; },
  (value) => { value.distribution.url = "https://github.com/z2six/saywhat/releases/download/v0.1.13/synthetic-distribution.zip"; },
  (value) => { value.source.url = "https://github.com/z2six/saywhat/releases/download/v0.1.14/source.exe"; },
  (value) => { value.distribution.url = "https://github.com/z2six/saywhat/releases/download/v0.1.14/distribution.html"; },
  (value) => { value.releaseUrl = "https://github.com/z2six/saywhat/releases/tag/v0.1.13"; },
  (value) => { value.repositoryUrl = "https://github.com/z2six/saywhat/releases"; }
]) {
  const invalid = structuredClone(ready);
  mutate(invalid);
  assertUnavailable(await browserFixture(invalid));
}

const boundary = await browserFixture(ready, false, { raw: JSON.stringify(ready).padEnd(32768, " ") });
check(boundary.elements.get("installer-link").getAttribute("aria-disabled") === null, "Exactly 32 KiB of valid UTF-8 JSON is accepted.");
check(boundary.stats.released, "Valid response releases its stream reader.");
const oversizedDeclared = await browserFixture(ready, false, { contentLength: "32769" });
assertUnavailable(oversizedDeclared);
check(oversizedDeclared.stats.reads === 0, "Oversized declared body is rejected before reading.");
check(oversizedDeclared.calls[0].options.signal.aborted, "Header rejection aborts the outstanding response.");
const bodyBytes = new TextEncoder().encode(JSON.stringify(ready).padEnd(32769, " "));
const oversizedActual = await browserFixture(ready, false, {
  contentLength: "1000", chunks: [bodyBytes.slice(0, 16384), bodyBytes.slice(16384, 32768), bodyBytes.slice(32768), new Uint8Array(10)]
});
assertUnavailable(oversizedActual);
check(oversizedActual.stats.reads === 3 && oversizedActual.stats.cancelled && oversizedActual.stats.released, "Actual-body limit stops at overflow, cancels the reader and releases it despite an understated header.");
assertUnavailable(await browserFixture(ready, false, { raw: "{" }));
assertUnavailable(await browserFixture(ready, false, { chunks: [new Uint8Array([255])] }));
assertUnavailable(await browserFixture(ready, false, { noBody: true }));
assertUnavailable(await browserFixture(ready, false, { ok: false }));
const stalled = await browserFixture(ready, false, { stall: true });
assertUnavailable(stalled);
check(stalled.calls[0].options.signal.aborted && stalled.stats.released, "A stalled body read is aborted by the same bounded timeout and releases its reader.");
console.log(`PASS: ${checks} offline website asset/anchor/accessibility-structure/release-gate checks. No network, server, app, model, audio or real clipboard.`);
