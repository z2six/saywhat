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
check(manifest.schemaVersion === 1 && typeof manifest.ready === "boolean", "Manifest readiness is an explicit versioned boolean.");
check(manifest.channel === "preview", "Preview status is explicit.");
if (manifest.securityNotice != null) check(manifest.securityNotice.affectedVersion === manifest.version &&
  ((manifest.version === "0.2.11" && manifest.securityNotice.id === "bitdefender-review-pending") ||
   (["0.2.12", "0.2.13"].includes(manifest.version) && manifest.securityNotice.id === "bitdefender-previous-build-review-pending" &&
    manifest.securityNotice.previousAffectedVersion === "0.2.11")), "A public security notice identifies the reviewed issue and exact affected release.");
check((html.match(/<main\b/g) || []).length === 1 && (html.match(/<\/main>/g) || []).length === 1, "Exactly one main landmark exists.");
check((html.match(/<h1\b/g) || []).length === 1, "One clear page heading exists.");
check(html.includes('lang="en"') && html.includes('class="skip-link"'), "English page and keyboard skip link exist.");
check(!/<style\b|<script\b[^>]*>\s*[^<\s]/.test(html), "No inline code bypasses the self-only content policy.");
const ids = Array.from(html.matchAll(/\bid="([^"]+)"/g), (match) => match[1]);
check(new Set(ids).size === ids.length, "Element IDs are unique.");
check(html.includes('id="release-security-notice" class="release-security-notice" role="note" aria-labelledby="release-security-title" hidden') &&
  html.indexOf('id="release-security-notice"') > html.indexOf('id="installer-link"') &&
  html.indexOf('id="release-security-notice"') < html.indexOf('class="release-links"'),
  "The download-adjacent security notice has a named note role and starts hidden until a validated release matches.");
check((html.match(/class="person-avatar"/g) || []).length === 2, "Their voices illustrates two people with separate photos.");
check(html.includes('assets/person-ari.svg') && html.includes('assets/person-mina.svg'), "Each illustrated person has their own local portrait.");
const hero = html.slice(html.indexOf('<section class="hero'), html.indexOf('</section>', html.indexOf('<section class="hero')));
check(hero.includes('data-i18n="hero.title">Translate Your Voice into VRChat</h1>') && !hero.includes('into VRChat chat</h1>'), "Hero states the requested own-voice outcome without an extra Chats suffix.");
check(hero.includes('class="app-illustration"') && hero.includes('class="mock-language-chips"') && hero.includes('data-i18n="demo.start"'), "Hero illustrates the current voice-first Home with visible language choices and its start action.");
const outputExample = hero.slice(hero.indexOf('class="scene-output"'), hero.indexOf('class="scene-subtitles"'));
check(outputExample.includes('lang="zh-Hans"') && outputExample.includes('lang="ja"'), "Own-voice output illustrates the two selected Chinese/Japanese targets.");
check((hero.match(/class="mini-bubble"/g) || []).length === 3 && (hero.match(/data-i18n="demo.person"/g) || []).length === 3,
  "The smaller optional overlay illustration shows three messages with individual person controls.");
const benefits = html.slice(html.indexOf('class="download-benefits"'), html.indexOf('class="download-disclosure"'));
check((benefits.match(/<article>/g) || []).length === 4 && html.indexOf('class="download-benefits"') > html.indexOf('id="checksum-panel"'), "Four clear product benefits follow the installer checksum.");
for (const topic of ['free', 'local', 'privacy', 'source']) check(benefits.includes(`data-i18n="benefits.${topic}Title"`) && benefits.includes(`data-i18n="benefits.${topic}Body"`), `The ${topic} benefit has a localized title and explanation.`);
check(benefits.includes('Setup downloads models; update checks use the internet.') && !benefits.includes('No internet connection'), "Local-processing copy keeps download and update network activity explicit.");
check(html.indexOf('id="setup"') < html.indexOf('id="speakers"') && html.indexOf('id="models"') < html.indexOf('id="speakers"'), "Core setup and model choice precede the optional People feature.");
const setup = html.slice(html.indexOf('<section id="setup"'), html.indexOf('</section>', html.indexOf('<section id="setup"')));
check((setup.match(/class="step-number"/g) || []).length === 4, "Four concise setup steps are visible.");
check(setup.indexOf('data-i18n="setup.languagesTitle"') < setup.indexOf('data-i18n="setup.modelsTitle"'), "Spoken and output languages are selected before model preparation.");
check(!/start your voice, subtitles, or both|choose your language, theme and translator/i.test(setup), "Optional features and theme selection are not mandatory setup decisions.");
check(!html.includes('WSL2 speech support'), "The website does not claim WSL is required for every recognition mode.");
check(html.includes('data-i18n="models.roles"') && html.includes('data-i18n="faq.sourceAnswer"'), "Recognition versus translation and source-language controls are explained.");
check(html.includes('data-i18n="models.nativeSpeech"') && html.includes('data-i18n="models.legacySpeech"') && html.includes('Nemotron Streaming') && html.includes('Voxtral Realtime'), "Windows-native and legacy WSL recognition choices are visible separately from translators.");
check(!/Moonshine|Paraformer/.test(html), "Unimplemented research candidates are not advertised as ready-to-use model downloads.");
check(html.includes('data-i18n="features.subtitlesLanguage">Choose spoken and subtitle languages. Use FireRedASR2 for Chinese/English or a multilingual recognizer.'), "Implemented FireRed subtitle support states its actual language scope and multilingual alternative.");
check(html.includes('TranslateGemma 4B</h4>') && !/TranslateGemma[^<]*12B|Alternative translators in two sizes/.test(html), "Only the verified TranslateGemma 4B download is advertised.");
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
  const documentEvents = new Map();
  let activeLanguage = "en";
  const catalogs = transport.catalogs;
  const localizer = catalogs ? {
    t: (key, parameters, fallback) => {
      const text = catalogs[activeLanguage]?.[key] || fallback;
      return text.replace(/\{(\w+)\}/g, (token, name) => parameters[name] ?? token);
    }
  } : null;
  vm.runInNewContext(script, {
    window: { SayWhatI18n: localizer },
    document: {
      getElementById: (id) => { check(elements.has(id), `Script target ${id} exists.`); return elements.get(id); },
      addEventListener: (name, handler) => { documentEvents.set(name, handler); }
    },
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
  return { elements, calls, stats, copied: () => copied,
    setLanguage: (language) => { activeLanguage = language; documentEvents.get("saywhat:languagechange")(); }
  };
}
function assertUnavailable(fixture) {
  for (const id of ["installer-link", "source-link", "distribution-link", "release-notes-link", "repository-link"]) {
    check(fixture.elements.get(id).getAttribute("aria-disabled") === "true", `${id} remains disabled.`);
    check(fixture.elements.get(id).href === "#release-status", `${id} does not expose an unavailable asset URL.`);
  }
  check(fixture.elements.get("checksum-panel").hidden, "Checksum panel is hidden without a verified-ready manifest.");
  check(fixture.elements.get("release-security-notice").hidden && fixture.elements.get("installer-link").getAttribute("aria-describedby") === null,
    "An unavailable or invalid release cannot leave a stale security notice attached to the download.");
}
const pending = await browserFixture({ ...manifest, ready: false });
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
check(available.elements.get("release-security-notice").hidden, "A release without the optional notice keeps the warning hidden.");
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

function releaseAtVersion(version) {
  const value = structuredClone(ready);
  value.version = version;
  value.releaseUrl = value.releaseUrl.replace(ready.version, version);
  for (const asset of ["installer", "source", "distribution"]) value[asset].url = value[asset].url.replaceAll(ready.version, version);
  return value;
}
const noticeRelease = releaseAtVersion("0.2.11");
noticeRelease.securityNotice = { id: "bitdefender-review-pending", affectedVersion: "0.2.11" };
const warned = await browserFixture(noticeRelease);
check(!warned.elements.get("release-security-notice").hidden && warned.elements.get("release-security-body").textContent.includes("0.2.11"),
  "The exact affected release displays its version-specific Bitdefender review notice.");
check(warned.elements.get("release-security-title").textContent === "Bitdefender review pending" &&
  warned.elements.get("release-security-body").textContent.includes("a verdict is pending") &&
  !/confirmed false positive|disable|turn off/i.test(warned.elements.get("release-security-body").textContent),
  "The notice discloses pending review without asserting a vendor verdict or telling users to disable protection.");
check(warned.elements.get("installer-link").getAttribute("aria-describedby") === "release-security-body" &&
  warned.elements.get("installer-link").href === noticeRelease.installer.url &&
  warned.elements.get("installer-link").getAttribute("aria-disabled") === null,
  "The available installer exposes its warning to assistive technology while preserving the verified download URL.");
for (const notice of [null, {}, [], "bitdefender-review-pending", { id: "unknown", affectedVersion: "0.2.11" },
  { id: "bitdefender-review-pending", affectedVersion: "0.2.10" }, { id: "<img onerror=alert(1)>", affectedVersion: "0.2.11" }]) {
  const unrelated = await browserFixture({ ...noticeRelease, securityNotice: notice });
  check(unrelated.elements.get("release-security-notice").hidden && unrelated.elements.get("installer-link").getAttribute("aria-describedby") === null,
    "Missing, malformed, unknown and mismatched optional notice metadata cannot display an unrelated or injected warning.");
}
for (const version of ["0.2.10", "0.2.12", "0.2.13"]) {
  const otherRelease = releaseAtVersion(version);
  otherRelease.securityNotice = { id: "bitdefender-review-pending", affectedVersion: version };
  check((await browserFixture(otherRelease)).elements.get("release-security-notice").hidden,
    "The known 0.2.11 issue is never carried over to another version.");
}
for (const invalid of [{ ...noticeRelease, ready: false }, { ...noticeRelease, source: { ...noticeRelease.source, sha256: "invalid" } }])
  assertUnavailable(await browserFixture(invalid));
const previousNoticeRelease = releaseAtVersion("0.2.12");
previousNoticeRelease.securityNotice = { id: "bitdefender-previous-build-review-pending", affectedVersion: "0.2.12", previousAffectedVersion: "0.2.11" };
const latestPreviousNoticeRelease = releaseAtVersion("0.2.13");
latestPreviousNoticeRelease.securityNotice = { id: "bitdefender-previous-build-review-pending", affectedVersion: "0.2.13", previousAffectedVersion: "0.2.11" };
for (const reviewedRelease of [previousNoticeRelease, latestPreviousNoticeRelease]) {
  const previousWarning = await browserFixture(reviewedRelease);
  check(!previousWarning.elements.get("release-security-notice").hidden &&
    previousWarning.elements.get("release-security-body").textContent.includes("flagged version 0.2.11") &&
    previousWarning.elements.get("release-security-body").textContent.includes(`Version ${reviewedRelease.version} has not received a vendor verdict`) &&
    !previousWarning.elements.get("release-security-body").textContent.includes(`currently flags a component in version ${reviewedRelease.version}`),
    "A reviewed preview distinguishes the confirmed predecessor detection from its unknown scan verdict.");
  for (const previous of ["0.2.10", "0.2.12", "0.2.13", null, "<img>"]) {
    const mismatch = structuredClone(reviewedRelease);
    mismatch.securityNotice.previousAffectedVersion = previous;
    check((await browserFixture(mismatch)).elements.get("release-security-notice").hidden,
      "The predecessor warning cannot carry an unreviewed or injected version.");
  }
}
const unreviewedNoticeRelease = releaseAtVersion("0.2.14");
unreviewedNoticeRelease.securityNotice = { id: "bitdefender-previous-build-review-pending", affectedVersion: "0.2.14", previousAffectedVersion: "0.2.11" };
check((await browserFixture(unreviewedNoticeRelease)).elements.get("release-security-notice").hidden,
  "The predecessor notice never silently applies to an unreviewed future release.");
await warned.elements.get("refresh-release").events.get("click")();
assertUnavailable(warned);

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
const current = await browserFixture(manifest);
if (manifest.ready) {
  check(current.elements.get("installer-link").getAttribute("aria-disabled") === null, "The published manifest passes the same strict client gate.");
  check(current.elements.get("installer-link").href === manifest.installer.url, "Current installer URL binds exactly.");
  check(current.elements.get("source-link").href === manifest.source.url, "Current corresponding-source URL binds exactly.");
  check(current.elements.get("distribution-link").href === manifest.distribution.url, "Current complete-ZIP URL binds exactly.");
} else assertUnavailable(current);
// Language changes must repaint current download state, never re-fetch or bypass its gate.
const localeCodes = ["en", "zh-CN", "ja", "ko", "es", "fr", "de", "pt-BR"];
const catalogs = Object.fromEntries(localeCodes.map((code) => [code, JSON.parse(readFileSync(resolve(root, "locales", `${code}.json`), "utf8"))]));
const localized = await browserFixture(ready, false, { catalogs });
const localizedPending = await browserFixture({ ...ready, ready: false }, false, { catalogs });
const localizedError = await browserFixture(null, true, { catalogs });
const localizedNotice = await browserFixture(noticeRelease, false, { catalogs });
const localizedPreviousNotice = await browserFixture(previousNoticeRelease, false, { catalogs });
const localizedLatestPreviousNotice = await browserFixture(latestPreviousNoticeRelease, false, { catalogs });
for (const code of localeCodes) {
  localized.setLanguage(code);
  check(localized.elements.get("installer-link").textContent === catalogs[code]["release.availableInstaller"], `${code}: ready button translates.`);
  check(localized.elements.get("release-status").textContent === catalogs[code]["release.availableStatus"].replace("{version}", ready.version), `${code}: ready status translates with version.`);
  check(localized.elements.get("installer-link").href === ready.installer.url && localized.calls.length === 1, `${code}: language switch preserves verified links without another request.`);
  await localized.elements.get("copy-checksum").events.get("click")();
  check(localized.elements.get("copy-status").textContent === catalogs[code]["release.checksumCopied"], `${code}: clipboard announcement translates.`);
  localizedPending.setLanguage(code);
  check(localizedPending.elements.get("release-status").textContent === catalogs[code]["release.pendingStatus"], `${code}: pending status translates.`);
  assertUnavailable(localizedPending);
  localizedError.setLanguage(code);
  check(localizedError.elements.get("release-status").textContent === catalogs[code]["release.errorStatus"], `${code}: failure status translates.`);
  assertUnavailable(localizedError);
  localizedNotice.setLanguage(code);
  check(!localizedNotice.elements.get("release-security-notice").hidden &&
    localizedNotice.elements.get("release-security-title").textContent === catalogs[code]["release.securityTitle"] &&
    localizedNotice.elements.get("release-security-body").textContent === catalogs[code]["release.securityBody"].replace("{version}", "0.2.11"),
    `${code}: the active version-specific security warning repaints in the selected language.`);
  check(localizedNotice.elements.get("installer-link").href === noticeRelease.installer.url && localizedNotice.calls.length === 1,
    `${code}: translating the warning neither replaces the installer nor triggers another manifest request.`);
  localizedPreviousNotice.setLanguage(code);
  check(!localizedPreviousNotice.elements.get("release-security-notice").hidden &&
    localizedPreviousNotice.elements.get("release-security-body").textContent === catalogs[code]["release.previousSecurityBody"]
      .replace("{previousVersion}", "0.2.11").replace("{version}", "0.2.12"),
    `${code}: the predecessor notice translates both exact versions without claiming a new detection.`);
  localizedLatestPreviousNotice.setLanguage(code);
  check(!localizedLatestPreviousNotice.elements.get("release-security-notice").hidden &&
    localizedLatestPreviousNotice.elements.get("release-security-body").textContent === catalogs[code]["release.previousSecurityBody"]
      .replace("{previousVersion}", "0.2.11").replace("{version}", "0.2.13"),
    `${code}: the 0.2.13 notice keeps the predecessor and unknown candidate verdict distinct.`);
}
console.log(`PASS: ${checks} offline website asset/anchor/accessibility-structure/release-gate checks. No network, server, app, model, audio or real clipboard.`);
