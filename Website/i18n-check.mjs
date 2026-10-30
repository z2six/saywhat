// Dependency-free offline localization checks. No browser, network or application
// process is started; the actual runtime executes in a VM with an inert DOM.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const root = new URL('./public/', import.meta.url);
const source = readFileSync(new URL('i18n.js', root), 'utf8');
const languages = ['en', 'zh-CN', 'ja', 'ko', 'es', 'fr', 'de', 'pt-BR'];
const catalogs = Object.fromEntries(languages.map(code => [code, JSON.parse(readFileSync(new URL(`locales/${code}.json`, root), 'utf8'))]));
const markup = {
  home: readFileSync(new URL('index.html', root), 'utf8'),
  error: readFileSync(new URL('404.html', root), 'utf8')
};
let checks = 0;
function check(value, description) { assert.ok(value, description); checks++; }
function equal(actual, expected, description) { assert.deepEqual(actual, expected, description); checks++; }
function tokens(value) { return value.match(/\{[A-Za-z][A-Za-z0-9_]*\}|<[^>]*>/g) || []; }
const englishKeys = Object.keys(catalogs.en).sort();
check(englishKeys.length >= 183, 'complete English catalog includes voice-first setup, product benefits and the current interface illustration');
for (const key of ['benefits.freeTitle', 'benefits.freeBody', 'benefits.localTitle', 'benefits.localBody',
  'benefits.privacyTitle', 'benefits.privacyBody', 'benefits.sourceTitle', 'benefits.sourceBody',
  'demo.home', 'demo.panelTitle', 'demo.speak', 'demo.translate', 'demo.start', 'demo.person', 'demo.caption',
  'release.securityTitle', 'release.securityBody'])
  check(englishKeys.includes(key), `new product copy has a stable catalog key: ${key}`);
for (const code of languages) {
  equal(Object.keys(catalogs[code]).sort(), englishKeys, `${code}: every English key has a translation`);
  for (const key of ['hero.local', 'release.pendingMeta', 'release.availableMeta'])
    check(catalogs[code][key].includes('Windows 10/11 x64'), `${code}: ${key} does not retain a Windows-11-only download claim`);
  check(catalogs[code]['requirements.windowsTitle'].includes('1809') && catalogs[code]['requirements.windowsTitle'].includes('Windows 11'), `${code}: requirements state the supported Windows 10 release and Windows 11`);
  check(catalogs[code]['requirements.windowsDescription'].includes('17763') && catalogs[code]['requirements.windowsDescription'].includes('Windows 11'), `${code}: detailed requirements keep the native own-voice floor separate from application-only capture`);
  check(['19041', '19044', '0.48.2'].every(version => catalogs[code]['requirements.speechDescription'].includes(version)), `${code}: optional WSL and CUDA speech keep their separate Windows and WSL requirements`);
  for (const key of englishKeys) {
    check(typeof catalogs[code][key] === 'string' && catalogs[code][key].trim().length > 0, `${code}: ${key} is nonempty text`);
    equal(tokens(catalogs[code][key]), tokens(catalogs.en[key]), `${code}: ${key} retains placeholders and limited rich markup`);
  }
}

const decode = text => text.replace(/&(?:amp|lt|gt|quot|apos|nbsp);|&#(?:x[0-9a-f]+|\d+);/gi, entity => {
  const named = { '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&apos;': "'", '&nbsp;': '\u00a0' };
  if (named[entity.toLowerCase()]) return named[entity.toLowerCase()];
  return String.fromCodePoint(entity[2].toLowerCase() === 'x' ? parseInt(entity.slice(3, -1), 16) : parseInt(entity.slice(2, -1), 10));
});
class TextNode {
  constructor(text) { this.nodeType = 3; this.textContent = text; this.childNodes = []; this.parentNode = null; }
}
class Container {
  constructor() { this.childNodes = []; this.parentNode = null; }
  appendChild(child) {
    if (child.nodeType === 11) { for (const nested of [...child.childNodes]) this.appendChild(nested); child.childNodes = []; return child; }
    if (child.parentNode) child.parentNode.childNodes = child.parentNode.childNodes.filter(item => item !== child);
    this.childNodes.push(child); child.parentNode = this; return child;
  }
  replaceChildren(...children) { for (const child of this.childNodes) child.parentNode = null; this.childNodes = []; for (const child of children) this.appendChild(child); }
  get textContent() { return this.childNodes.map(child => child.textContent).join(''); }
  set textContent(text) { this.replaceChildren(new TextNode(String(text))); }
  querySelectorAll(selector) {
    const selectors = selector.split(',').map(part => part.trim());
    const matches = node => node.nodeType === 1 && selectors.some(part => part.startsWith('[')
      ? node.hasAttribute(part.slice(1, -1)) : node.tagName.toLowerCase() === part.toLowerCase());
    const found = [];
    const visit = node => { for (const child of node.childNodes) { if (matches(child)) found.push(child); visit(child); } };
    visit(this); return found;
  }
}
class Element extends Container {
  constructor(tag, attrs = {}) { super(); this.nodeType = 1; this.tagName = tag.toUpperCase(); this.attrs = { ...attrs }; this.listeners = new Map(); this.value = attrs.value || ''; this.hidden = 'hidden' in attrs; }
  getAttribute(name) { return Object.hasOwn(this.attrs, name) ? this.attrs[name] : null; }
  hasAttribute(name) { return Object.hasOwn(this.attrs, name); }
  setAttribute(name, value) { this.attrs[name] = String(value); }
  cloneNode(deep = false) { const clone = new Element(this.tagName, this.attrs); if (deep) for (const child of this.childNodes) clone.appendChild(child.nodeType === 3 ? new TextNode(child.textContent) : child.cloneNode(true)); return clone; }
  addEventListener(type, listener) { if (!this.listeners.has(type)) this.listeners.set(type, []); this.listeners.get(type).push(listener); }
  dispatchEvent(event) { for (const listener of this.listeners.get(event.type) || []) listener(event); }
}
class Fragment extends Container { constructor() { super(); this.nodeType = 11; } }
class Document extends Container {
  constructor(html, options) {
    super(); this.nodeType = 9; this.events = []; this.listeners = new Map(); this.cookieWrites = []; this.cookieValue = options.cookie || ''; this.options = options;
    const stack = [this]; const voidTags = new Set(['area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr']);
    for (const token of html.match(/<!--[\s\S]*?-->|<![^>]*>|<\/?[A-Za-z][^>]*>|[^<]+/g) || []) {
      if (token.startsWith('<!')) continue;
      if (token.startsWith('</')) { stack.pop(); continue; }
      if (token.startsWith('<')) {
        const tag = token.match(/^<([A-Za-z][\w-]*)/)[1].toLowerCase(); const attrs = {};
        const rest = token.slice(token.indexOf(tag) + tag.length, -1);
        for (const match of rest.matchAll(/([\w:-]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?/g)) attrs[match[1]] = decode(match[2] ?? match[3] ?? match[4] ?? '');
        const node = new Element(tag, attrs); stack.at(-1).appendChild(node);
        if (!voidTags.has(tag) && !token.endsWith('/>')) stack.push(node);
      } else stack.at(-1).appendChild(new TextNode(decode(token)));
    }
    this.documentElement = this.querySelectorAll('html')[0];
  }
  getElementById(id) { const visit = node => { if (node.nodeType === 1 && node.getAttribute('id') === id) return node; for (const child of node.childNodes) { const found = visit(child); if (found) return found; } }; return visit(this) || null; }
  createDocumentFragment() { return new Fragment(); }
  createElement(tag) { return new Element(tag); }
  createTextNode(text) { return new TextNode(text); }
  get title() { return this.querySelectorAll('title')[0]?.textContent || ''; }
  get cookie() { if (this.options.blockCookieRead) throw new Error('Cookie access blocked'); return this.cookieValue; }
  set cookie(value) {
    this.cookieWrites.push(value); if (this.options.blockCookieWrite) throw new Error('Cookie access blocked');
    const [pair] = value.split(';'); const name = pair.split('=')[0];
    const previous = this.cookieValue.split(';').map(part => part.trim()).filter(part => part && !part.startsWith(`${name}=`));
    this.cookieValue = [...previous, pair].join('; ');
  }
  addEventListener(type, listener) { if (!this.listeners.has(type)) this.listeners.set(type, []); this.listeners.get(type).push(listener); }
  dispatchEvent(event) { this.events.push(event); for (const listener of this.listeners.get(event.type) || []) listener(event); }
}
const annotated = document => document.querySelectorAll('[data-i18n], [data-i18n-aria-label], [data-i18n-content], [data-i18n-title]');
function richText(node) { return node.childNodes.map(child => child.nodeType === 3 ? child.textContent : child.tagName === 'BR' ? '<br>' : ['STRONG', 'A'].includes(child.tagName) ? `<${child.tagName.toLowerCase()}>${richText(child)}</${child.tagName.toLowerCase()}>` : child.textContent).join(''); }
for (const [page, html] of Object.entries(markup)) {
  const document = new Document(html, {});
  for (const node of annotated(document)) {
    for (const attribute of ['data-i18n', 'data-i18n-aria-label', 'data-i18n-content', 'data-i18n-title']) {
      const key = node.getAttribute(attribute); if (!key) continue;
      check(Object.hasOwn(catalogs.en, key), `${page}: ${attribute} references catalog key ${key}`);
      const original = attribute === 'data-i18n' ? node.hasAttribute('data-i18n-rich') ? richText(node) : node.textContent : node.getAttribute(attribute.slice('data-i18n-'.length));
      equal(original, catalogs.en[key], `${page}: English fallback markup matches ${key}`);
    }
  }
  // Every meaningful text leaf must be localized or explicitly carry its fixed
  // language (brands, language autonyms and illustrated utterances).
  const visit = node => {
    if (node.nodeType === 3 && /\p{L}/u.test(node.textContent)) {
      let parent = node.parentNode; let explained = false;
      while (parent?.nodeType === 1) { if (parent.hasAttribute('data-i18n') || parent.getAttribute('aria-hidden') === 'true' || parent.hasAttribute('lang') && parent.tagName !== 'HTML' || ['SCRIPT', 'STYLE'].includes(parent.tagName)) { explained = true; break; } parent = parent.parentNode; }
      check(explained, `${page}: text is translated or explicitly fixed-language: ${node.textContent.trim().slice(0, 80)}`);
    }
    for (const child of node.childNodes) visit(child);
  };
  visit(document);
}

function harness(options = {}) {
  const document = new Document(markup[options.page || 'home'], options); const requests = []; const timers = new Map(); let nextTimer = 0;
  const location = { href: options.url || 'https://saywhat.example/', get protocol() { return new URL(this.href).protocol; } };
  const window = { location, listeners: new Map(), history: { state: null, replacements: [], replaceState(state, unused, href) { this.state = state; this.replacements.push(href); location.href = href; } }, addEventListener(type, listener) { this.listeners.set(type, listener); } };
  const fetch = (url, fetchOptions) => new Promise((resolve, reject) => {
    const request = { url, options: fetchOptions, resolve, reject, settled: false }; requests.push(request);
    if (!options.ignoreAbort) fetchOptions.signal.addEventListener('abort', () => { if (!request.settled) { request.settled = true; reject(new Error('Aborted')); } });
  });
  const context = { document, window, navigator: { languages: options.languages || ['en-US'], language: options.language || 'en-US' }, fetch, URL, AbortController, TextDecoder, TextEncoder,
    CustomEvent: class { constructor(type, init) { this.type = type; this.detail = init.detail; } },
    setTimeout(fn, delay) { const id = ++nextTimer; timers.set(id, { fn, delay }); return id; }, clearTimeout(id) { timers.delete(id); } };
  vm.runInNewContext(source, context, { filename: 'public/i18n.js', timeout: 1000 });
  function request(code) { return requests.find(item => !item.settled && item.url === `/locales/${code}.json`); }
  function reply(codeOrRequest, catalog = catalogs[typeof codeOrRequest === 'string' ? codeOrRequest : 'en'], config = {}) {
    const item = typeof codeOrRequest === 'string' ? request(codeOrRequest) : codeOrRequest;
    assert.ok(item, `Expected pending locale request ${codeOrRequest}`);
    item.settled = true; const raw = config.bytes || new TextEncoder().encode(config.raw ?? JSON.stringify(catalog));
    const chunks = config.chunks || [raw]; let index = 0; item.cancelled = false; item.released = false;
    item.resolve({ ok: config.ok !== false, headers: { get(name) { return name === 'Content-Length' ? config.declared || null : null; } },
      body: config.noBody ? null : { getReader() { item.read = true; return { async read() { return index < chunks.length ? { done: false, value: chunks[index++] } : { done: true }; }, async cancel() { item.cancelled = true; }, releaseLock() { item.released = true; } }; } } });
    return item;
  }
  const reject = (code, error = new Error('Offline fixture unavailable')) => { const item = request(code); assert.ok(item); item.settled = true; item.reject(error); };
  const expire = () => { const pending = [...timers.values()]; timers.clear(); for (const timer of pending) { equal(timer.delay, 5000, 'catalog timeout is bounded to five seconds'); timer.fn(); } };
  return { document, window, api: window.SayWhatI18n, requests, request, reply, reject, expire };
}
const settle = async () => { for (let index = 0; index < 18; index++) await Promise.resolve(); };

for (const [options, expected] of [
  [{ languages: ['xx-XX', 'fr-CA', 'en-US'] }, 'fr'],
  [{ languages: ['zh-Hant-TW', 'ja-JP'] }, 'zh-CN'],
  [{ languages: ['pt-PT'] }, 'pt-BR'],
  [{ languages: ['ko_KR'] }, 'ko'],
  [{ languages: ['de-DE', 'es-MX'] }, 'de'],
  [{ languages: ['xx', 'invalid!'], language: 'ja' }, 'en'],
  [{ languages: [], language: 'ja-JP' }, 'ja'],
  [{ url: 'https://saywhat.example/?lang=ko-KR', cookie: '__Host-saywhat_lang=ja', languages: ['de'] }, 'ko'],
  [{ url: 'https://saywhat.example/?lang=unknown', cookie: '__Host-saywhat_lang=ja', languages: ['de'] }, 'ja'],
  [{ cookie: 'saywhat_lang=es-MX', languages: ['de'] }, 'es'],
  [{ cookie: '__Host-saywhat_lang=ko; saywhat_lang=ja', languages: ['de'] }, 'ko'],
  [{ cookie: '__Host-saywhat_lang=%not-valid', languages: ['de-DE'] }, 'de'],
  [{ blockCookieRead: true, languages: ['fr'] }, 'fr']
]) {
  const fixture = harness(options); equal(fixture.api.preferredLanguage(), expected, 'URL, saved cookie and ordered browser preference precedence');
  equal(fixture.document.cookieWrites.length, 0, 'automatic/URL/cookie selection does not persist a new cookie');
  for (const item of fixture.requests) {
    check(/^\/locales\/(?:en|zh-CN|ja|ko|es|fr|de|pt-BR)\.json$/.test(item.url), 'unsupported language is never used as a fetch path');
    equal(item.options.credentials, 'omit', 'catalog request sends no preference cookie');
    equal(item.options.redirect, 'error', 'catalog request cannot redirect to an unexpected origin');
    equal(item.options.cache, 'no-cache', 'catalog is revalidated after deployment rather than kept indefinitely stale');
  }
}

{
  const fixture = harness({ url: 'https://saywhat.example/?existing=one#models' }); fixture.reply('en'); await settle();
  const before = fixture.requests.length; await fixture.api.setLanguage('../../secret'); equal(fixture.requests.length, before, 'invalid locale falls back to cached English without a fetch');
  const selector = fixture.document.getElementById('website-language'); selector.value = 'ja'; selector.dispatchEvent({ type: 'change' });
  equal(fixture.document.cookieWrites.length, 1, 'explicit picker choice writes one functional cookie');
  equal(fixture.document.cookieWrites[0], '__Host-saywhat_lang=ja; Max-Age=31536000; Path=/; SameSite=Lax; Secure', 'HTTPS cookie is host-only, Secure and one year');
  const url = new URL(fixture.window.location.href); equal(url.searchParams.get('existing'), 'one', 'language choice preserves other query parameters'); equal(url.hash, '#models', 'language choice preserves the section hash'); equal(url.searchParams.get('lang'), 'ja', 'language choice creates a shareable URL');
  fixture.reply('ja'); await settle(); equal(fixture.api.language, 'ja', 'manual language request renders selected catalog');
  equal(selector.value, 'ja', 'picker reflects rendered language');
  equal(fixture.document.getElementById('language-status').hidden, true, 'successful switch hides language-load feedback');
}
{
  const fixture = harness({ url: 'http://localhost/?keep=1', blockCookieRead: true, blockCookieWrite: true }); fixture.reply('en'); await settle();
  const pending = fixture.api.setLanguage('ko', { persist: true, updateUrl: true }); fixture.reply('ko'); await pending;
  equal(fixture.api.language, 'ko', 'blocked cookies do not prevent a manual switch');
  equal(fixture.document.cookieWrites[0], 'saywhat_lang=ko; Max-Age=31536000; Path=/; SameSite=Lax', 'HTTP preview uses unprefixed functional cookie without pretending it is Secure');
}

for (const page of ['home', 'error']) {
  const fixture = harness({ page }); fixture.reply('en'); await settle();
  const originalLinks = fixture.document.querySelectorAll('a').filter(node => node.parentNode?.hasAttribute('data-i18n-rich')).map(node => ({ href: node.getAttribute('href'), target: node.getAttribute('target'), rel: node.getAttribute('rel') }));
  for (const code of languages) {
    const pending = fixture.api.setLanguage(code); if (fixture.request(code)) fixture.reply(code); await pending;
    equal(fixture.document.documentElement.getAttribute('lang'), code, `${page}/${code}: document language updates`);
    for (const node of annotated(fixture.document)) {
      const key = node.getAttribute('data-i18n');
      if (key) equal(node.hasAttribute('data-i18n-rich') ? richText(node) : node.textContent, catalogs[code][key], `${page}/${code}: rendered ${key}`);
      for (const attribute of ['aria-label', 'content', 'title']) { const attributeKey = node.getAttribute(`data-i18n-${attribute}`); if (attributeKey) equal(node.getAttribute(attribute), catalogs[code][attributeKey], `${page}/${code}: ${attribute} translated`); }
    }
    equal(fixture.document.title, catalogs[code][page === 'home' ? 'page.title' : 'notFound.title'], `${page}/${code}: title translated`);
    const currentLinks = fixture.document.querySelectorAll('a').filter(node => node.parentNode?.hasAttribute('data-i18n-rich')).map(node => ({ href: node.getAttribute('href'), target: node.getAttribute('target'), rel: node.getAttribute('rel') }));
    equal(currentLinks, originalLinks, `${page}/${code}: original rich link destinations and isolation attributes survive`);
    const formatted = fixture.api.t('release.availableMeta', { version: '0.1.15', size: '123' }); check(formatted.includes('0.1.15') && formatted.includes('123') && !formatted.includes('{version}') && !formatted.includes('{size}'), `${code}: release parameters expand without changing keys`);
    const securityNotice = fixture.api.t('release.securityBody', { version: '0.2.11' });
    check(securityNotice.includes('0.2.11') && !securityNotice.includes('{version}'), `${code}: the pending vendor-review notice expands the exact affected version`);
  }
}

for (const configuration of [{ ok: false }, { noBody: true }, { raw: '{bad' }, { raw: '[]' }, { raw: '{}' }, { bytes: new Uint8Array([255]) }, { raw: '{"hero.title":2}' }, { raw: '{"hero.title":" "}' }, { declared: '204801' }, { chunks: [new Uint8Array(204801)] }]) {
  const fixture = harness({ languages: ['ja-JP'] }); fixture.reply('en'); await settle();
  const response = fixture.reply('ja', catalogs.ja, configuration); await settle();
  equal(fixture.api.language, 'en', 'absent/malformed/oversized catalog falls back to visible English');
  equal(fixture.document.querySelectorAll('h1')[0].textContent, catalogs.en['hero.title'], 'fallback page content remains visible with the requested title');
  equal(fixture.document.getElementById('language-status').hidden, false, 'failed catalog has visible user feedback');
  equal(fixture.document.getElementById('language-status').textContent, catalogs.en['language.unavailable'], 'failed catalog uses explicit fallback explanation');
  check(response.options.signal.aborted, 'failed catalog aborts any remaining work');
  if (configuration.chunks) check(response.cancelled && response.released, 'oversized stream is cancelled and its reader is released');
}
{
  const fixture = harness({ languages: ['ko'] }); fixture.reply('en'); await settle(); fixture.expire(); await settle();
  equal(fixture.api.language, 'en', 'deadline falls back to English'); equal(fixture.document.getElementById('language-status').hidden, false, 'timeout exposes fallback feedback');
}
{
  const fixture = harness({ languages: ['ja'] }); fixture.reject('ja'); await settle();
  equal(fixture.document.getElementById('language-status').hidden, false, 'initial failure feedback is visible before English completion');
  fixture.reply('en'); await settle();
  equal(fixture.document.getElementById('language-status').hidden, false, 'late English catalog must not erase locale failure feedback');
}
{
  const fixture = harness(); fixture.reject('en'); await fixture.api.ready;
  equal(fixture.api.language, 'en', 'missing independent English catalog leaves the markup fallback usable');
  equal(fixture.document.title, catalogs.en['page.title'], 'missing English catalog preserves title');
  equal(fixture.api.t('release.checking'), catalogs.en['release.checking'], 'dynamic release defaults remain available without catalogs');
  equal(fixture.document.cookieWrites.length, 0, 'missing catalog does not create a preference cookie');
}
{
  const fixture = harness({ languages: ['ja'] }); const selector = fixture.document.getElementById('website-language'); selector.value = 'ja';
  fixture.reply('en'); await settle(); equal(selector.value, 'ja', 'English catalog completion does not reset an in-flight picker');
  fixture.reply('ja'); await settle(); equal(fixture.api.language, 'ja', 'independent English completion does not win a locale request');
}
{
  const fixture = harness({ ignoreAbort: true }); fixture.reply('en'); await settle();
  const first = fixture.api.setLanguage('ja'); const stale = fixture.request('ja');
  const second = fixture.api.setLanguage('ko'); check(stale.options.signal.aborted, 'new switch aborts old locale work');
  fixture.reply('ko'); await second; fixture.reply(stale, catalogs.ja); await first;
  equal(fixture.api.language, 'ko', 'late aborted response cannot overwrite the newest locale');
  equal(fixture.document.title, catalogs.ko['page.title'], 'late response does not replace translated title');
  equal(fixture.document.cookieWrites.length, 0, 'programmatic switch without persist never writes a cookie');
}
{
  const fixture = harness(); fixture.reply('en'); await settle();
  const originalImageCount = fixture.document.querySelectorAll('img').length;
  const malicious = { ...catalogs.ja, 'hero.description': '<img src=x onerror=alert(1)>', 'faq.oscAnswer': '<a href="https://evil.invalid">unsafe</a>' };
  const pending = fixture.api.setLanguage('ja'); fixture.reply('ja', malicious); await pending;
  equal(fixture.document.querySelectorAll('img').length, originalImageCount, 'plain translation text cannot inject an image');
  equal(fixture.document.querySelectorAll('p').find(node => node.getAttribute('data-i18n') === 'hero.description').textContent, malicious['hero.description'], 'plain markup is displayed literally, not parsed');
  const faq = fixture.document.querySelectorAll('p').find(node => node.getAttribute('data-i18n') === 'faq.oscAnswer');
  equal(richText(faq), catalogs.en['faq.oscAnswer'], 'invalid rich markup falls back to safe English structure');
  equal(faq.querySelectorAll('a')[0].getAttribute('href'), 'https://docs.vrchat.com/docs/osc-overview', 'malicious catalog cannot change original rich link URL');
}
{
  const fixture = harness({ cookie: '__Host-saywhat_lang=ko', languages: ['ja'] }); fixture.reply('en'); fixture.reply('ko'); await settle();
  fixture.window.location.href = 'https://saywhat.example/?lang=fr#help'; fixture.window.listeners.get('popstate')(); fixture.reply('fr'); await settle();
  equal(fixture.api.language, 'fr', 'history navigation reapplies URL-first language preference'); equal(fixture.document.cookieWrites.length, 0, 'history and automatic selection do not rewrite preference cookies');
}

console.log(`PASS: ${checks} offline localization checks across ${languages.length} catalogs, home + 404, cookie/URL precedence, failures, races and safe DOM links. No network, browser, audio or models used.`);
