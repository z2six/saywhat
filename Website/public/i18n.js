(() => {
  "use strict";

  const supported = Object.freeze(["en", "zh-CN", "ja", "ko", "es", "fr", "de", "pt-BR"]);
  const selector = document.getElementById("website-language");
  const languageStatus = document.getElementById("language-status");
  const catalogLimit = 200 * 1024;
  const cookieAge = 365 * 24 * 60 * 60;
  const own = (object, key) => Object.prototype.hasOwnProperty.call(object, key);
  const catalogs = new Map();
  let language = "en";
  let switchNumber = 0;
  let localeController = null;
  let failedLanguage = null;

  // These dynamic defaults keep the release check independent of catalog loading.
  let english = Object.assign(Object.create(null), {
    "release.pendingStatus": "The public installer is being prepared. Downloads are not available yet.",
    "release.pendingInstaller": "Installer being prepared ↓",
    "release.pendingMeta": "Preview · Windows 11 x64 · Unsigned installer",
    "release.availableStatus": "Preview {version} is available. Review the requirements before installing.",
    "release.availableInstaller": "Download for Windows ↓",
    "release.availableMeta": "Version {version} · {size} MiB · Windows 11 x64 · Unsigned installer",
    "release.errorStatus": "Download status could not be checked. Try refreshing; no installer link has been enabled.",
    "release.checking": "Checking download status…",
    "release.refresh": "Refresh download status",
    "release.checksumCopied": "Checksum copied.",
    "release.checksumCopyFailed": "Could not copy. Select and copy the checksum above.",
    "language.unavailable": "This language could not be loaded. English is shown; choose a language to try again."
  });

  function serializeRich(node) {
    return Array.from(node.childNodes, (child) => {
      if (child.nodeType === 3) return child.textContent;
      if (child.nodeType !== 1) return "";
      const tag = child.tagName.toLowerCase();
      if (tag === "br") return "<br>";
      if (tag === "strong" || tag === "a") return `<${tag}>${serializeRich(child)}</${tag}>`;
      return child.textContent;
    }).join("");
  }

  const records = [];
  for (const node of document.querySelectorAll("[data-i18n], [data-i18n-aria-label], [data-i18n-content], [data-i18n-title]")) {
    const key = node.getAttribute("data-i18n");
    if (key) {
      const rich = node.hasAttribute("data-i18n-rich");
      const original = rich ? serializeRich(node) : node.textContent;
      english[key] = original;
      records.push({ node, key, rich, original, links: rich ? Array.from(node.querySelectorAll("a"), (link) => link.cloneNode(false)) : [] });
    }
    for (const attribute of ["aria-label", "content", "title"]) {
      const attributeKey = node.getAttribute(`data-i18n-${attribute}`);
      if (!attributeKey) continue;
      const original = node.getAttribute(attribute) || "";
      english[attributeKey] = original;
      records.push({ node, key: attributeKey, attribute, original });
    }
  }
  catalogs.set("en", english);

  function normalizeLanguage(value) {
    if (typeof value !== "string") return null;
    const candidate = value.trim().replace(/_/g, "-").toLowerCase();
    if (!/^[a-z]{2,3}(?:-[a-z0-9]{2,8})*$/.test(candidate)) return null;
    const base = candidate.split("-")[0];
    if (base === "zh") return "zh-CN";
    if (base === "pt") return "pt-BR";
    return supported.includes(base) ? base : null;
  }

  function cookieLanguage() {
    try {
      const cookies = document.cookie.split(";").map((entry) => entry.trim());
      for (const name of ["__Host-saywhat_lang", "saywhat_lang"]) {
        const entry = cookies.find((cookie) => cookie.startsWith(`${name}=`));
        if (!entry) continue;
        const result = normalizeLanguage(decodeURIComponent(entry.slice(name.length + 1)));
        if (result) return result;
      }
    } catch { /* Browser cookie restrictions do not prevent a language switch. */ }
    return null;
  }

  function preferredLanguage() {
    try {
      const explicit = normalizeLanguage(new URL(window.location.href).searchParams.get("lang"));
      if (explicit) return explicit;
    } catch { /* An invalid URL falls back to the saved/browser preference. */ }
    const saved = cookieLanguage();
    if (saved) return saved;
    const browserLanguages = Array.isArray(navigator.languages) && navigator.languages.length ? navigator.languages : [navigator.language];
    for (const candidate of browserLanguages) {
      const result = normalizeLanguage(candidate);
      if (result) return result;
    }
    return "en";
  }

  function rememberLanguage(value) {
    try {
      const secure = window.location.protocol === "https:";
      const name = secure ? "__Host-saywhat_lang" : "saywhat_lang";
      document.cookie = `${name}=${encodeURIComponent(value)}; Max-Age=${cookieAge}; Path=/; SameSite=Lax${secure ? "; Secure" : ""}`;
    } catch { /* The current page still changes when cookies are unavailable. */ }
  }

  function updateLanguageUrl(value) {
    try {
      const url = new URL(window.location.href);
      url.searchParams.set("lang", value);
      window.history.replaceState(window.history.state, "", url.href);
    } catch { /* Language selection also works in restricted preview contexts. */ }
  }

  function t(key, params = {}, fallback = "") {
    const catalog = catalogs.get(language);
    const text = catalog && own(catalog, key) ? catalog[key] : own(english, key) ? english[key] : fallback;
    return String(text).replace(/\{([A-Za-z][A-Za-z0-9_]*)\}/g, (token, name) => own(params, name) ? String(params[name]) : token);
  }

  function richFragment(record, value) {
    // A translation can change words, but cannot create attributes or new links.
    const tokens = value.match(/<[^>]*>/g) || [];
    const expected = record.original.match(/<[^>]*>/g) || [];
    const normalizeToken = (token) => /^<br\s*\/?\s*>$/.test(token) ? "<br>" : token;
    if (tokens.length !== expected.length || tokens.some((token, index) => normalizeToken(token) !== normalizeToken(expected[index]))) return null;
    const fragment = document.createDocumentFragment();
    const stack = [fragment];
    let linkIndex = 0;
    for (const part of value.split(/(<[^>]*>)/g)) {
      if (!part) continue;
      if (part === "<strong>" || part === "<a>") {
        const tag = part === "<a>" ? "a" : "strong";
        const child = tag === "a" ? record.links[linkIndex++]?.cloneNode(false) : document.createElement("strong");
        if (!child) return null;
        stack[stack.length - 1].appendChild(child);
        stack.push(child);
      } else if (part === "</strong>" || part === "</a>") {
        if (stack.length < 2 || stack[stack.length - 1].tagName.toLowerCase() !== part.slice(2, -1)) return null;
        stack.pop();
      } else if (normalizeToken(part) === "<br>") {
        stack[stack.length - 1].appendChild(document.createElement("br"));
      } else {
        if (part.includes("<") || part.includes(">")) return null;
        stack[stack.length - 1].appendChild(document.createTextNode(part));
      }
    }
    return stack.length === 1 ? fragment : null;
  }

  function render(requestedLanguage = language, failed = false) {
    document.documentElement.setAttribute("lang", language);
    if (selector) selector.value = language;
    for (const record of records) {
      const value = t(record.key, {}, record.original);
      if (record.attribute) record.node.setAttribute(record.attribute, value);
      else if (record.rich) {
        const translated = richFragment(record, value);
        const fallback = translated || richFragment(record, own(english, record.key) ? english[record.key] : record.original);
        if (fallback) record.node.replaceChildren(fallback);
        else record.node.textContent = record.original.replace(/<[^>]*>/g, "");
      } else record.node.textContent = value;
    }
    if (languageStatus) {
      languageStatus.hidden = !failed;
      languageStatus.textContent = failed ? t("language.unavailable") : "";
    }
    document.dispatchEvent(new CustomEvent("saywhat:languagechange", { detail: { language, requestedLanguage, fallback: failed } }));
  }

  async function readCatalog(code, controller) {
    const timeout = setTimeout(() => controller.abort(), 5000);
    try {
      const response = await fetch(`/locales/${code}.json`, { credentials: "omit", cache: "no-cache", redirect: "error", signal: controller.signal });
      if (!response.ok) throw new Error("Language catalog unavailable");
      const declared = response.headers.get("Content-Length");
      if (declared && /^\d+$/.test(declared) && Number(declared) > catalogLimit) throw new Error("Language catalog exceeds its size limit");
      if (!response.body) throw new Error("Language catalog has no body");
      const reader = response.body.getReader();
      const chunks = [];
      let total = 0;
      try {
        while (true) {
          if (controller.signal.aborted) throw new Error("Language request expired");
          const { done, value } = await reader.read();
          if (done) break;
          total += value.byteLength;
          if (total > catalogLimit) {
            reader.cancel().catch(() => {});
            throw new Error("Language catalog exceeds its size limit");
          }
          if (value.byteLength) chunks.push(value);
        }
      } finally { reader.releaseLock(); }
      const bytes = new Uint8Array(total);
      let offset = 0;
      for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
      const parsed = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
      if (!parsed || typeof parsed !== "object" || Array.isArray(parsed) || !Object.keys(parsed).length || Object.values(parsed).some((value) => typeof value !== "string" || !value.trim())) throw new Error("Invalid language catalog");
      return Object.assign(Object.create(null), parsed);
    } catch (error) {
      controller.abort();
      throw error;
    } finally { clearTimeout(timeout); }
  }

  async function setLanguage(value, { persist = false, updateUrl = false } = {}) {
    const code = normalizeLanguage(value) || "en";
    const currentSwitch = ++switchNumber;
    if (localeController) localeController.abort();
    localeController = null;
    failedLanguage = null;
    if (persist) rememberLanguage(code);
    if (updateUrl) updateLanguageUrl(code);
    if (languageStatus) { languageStatus.hidden = true; languageStatus.textContent = ""; }
    if (catalogs.has(code)) {
      language = code;
      render(code);
      return language;
    }
    const controller = new AbortController();
    localeController = controller;
    try {
      const catalog = await readCatalog(code, controller);
      if (currentSwitch !== switchNumber || controller.signal.aborted) return language;
      catalogs.set(code, catalog);
      language = code;
      render(code);
    } catch {
      if (currentSwitch !== switchNumber) return language;
      language = "en";
      failedLanguage = code;
      render(code, true);
    } finally {
      if (localeController === controller) localeController = null;
    }
    return language;
  }

  const api = { t, setLanguage, normalizeLanguage, preferredLanguage, supported, get language() { return language; }, ready: null };
  window.SayWhatI18n = api;
  if (selector) selector.addEventListener("change", () => { void setLanguage(selector.value, { persist: true, updateUrl: true }); });
  window.addEventListener("popstate", () => { void setLanguage(preferredLanguage()); });

  // English markup is usable immediately even if this independent fetch fails.
  const englishReady = readCatalog("en", new AbortController()).then((catalog) => {
    english = Object.assign(Object.create(null), english, catalog);
    catalogs.set("en", english);
    if (language === "en" && !localeController) render(failedLanguage || language, Boolean(failedLanguage));
  }).catch(() => {});
  api.ready = Promise.all([englishReady, setLanguage(preferredLanguage())]).then(() => language);
})();
