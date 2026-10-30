#!/usr/bin/env node
// Inspect exactly the indexed blobs that Git would publish. No network, user
// data directory, application, model or capture device is accessed.
import { spawnSync } from 'node:child_process';
import { lstatSync, realpathSync } from 'node:fs';
import { dirname, posix, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const maxFileBytes = 10 * 1024 * 1024;
const allowedModes = new Set(['100644', '100755']);
const assetExtensions = /\.(?:png|jpe?g|gif|webp|ico|bmp|ttf|otf|woff2?)$/i;
const binaryExtensions = /\.(?:exe|dll|pdb|zip|tar|tgz|gz|bz2|7z|gguf|onnx|safetensors|pt|pth|ckpt|bin|wav|mp3|flac|ogg|m4a|mp4|webm|log|dmp|cache|bak|backup|tmp|user|suo|key|pem|pfx|p12|kdbx)$/i;
const privateBaseNames = new Set([
  'vrchat-translation-settings.json', 'own-voice-runtime.json', 'incoming-speaker-feedback.json',
  'incoming-ai-speaker-profiles.json', 'provisioning.json', 'setup-progress.json',
  'own-voice-setup-progress.json', 'incoming-voice-setup-progress.json',
  'ui-language.json', 'credentials', 'credentials.json', 'credentials.toml',
  'secrets.json', 'secrets.toml', 'auth-setup.md', '.npmrc', '.pypirc', '.ds_store',
]);
const privateDirectories = /^(?:\.git|\.aws|\.azure|\.ssh|\.codex|\.agents|\.wrangler|node_modules|artifacts|bin|obj|dist|publish|runtimes|downloads|models|qa|data|test-artifacts|test-results|people-photos|recordings|captures|offline-.+|build-.+|release-export-.+|.+-publish(?:-.+)?|.+-checks?(?:-.+)?|.+-backup(?:-.+)?)$/i;

function pathProblems(name, mode = '100644', bytes = 0, linked = false) {
  const problems = [];
  const normalized = name.replaceAll('\\', '/');
  const parts = normalized.split('/');
  const base = parts.at(-1).toLowerCase();
  if (normalized !== name || normalized.startsWith('/') || parts.some(part => !part || part === '.' || part === '..') || /[\x00-\x1f\x7f]/.test(name))
    problems.push('invalid repository path');
  if (!allowedModes.has(mode) || linked) problems.push('symlink or submodule');
  if (parts.slice(0, -1).some(part => privateDirectories.test(part))) problems.push('generated or private directory');
  if (privateBaseNames.has(base.replace(/\.tmp-[a-f0-9]{32}$/i, '')) || /^saywhat-support-.*\.txt(?:\.tmp-[a-f0-9]{32})?$/i.test(base) || /^\.env(?:\.|$)/i.test(base) || /^\.dev\.vars/i.test(base) ||
      /^config(?:\..+)?\.jsonc$/i.test(base) && !/^config\.example\.jsonc$/i.test(base))
    problems.push('runtime preferences or credentials file');
  if (binaryExtensions.test(base)) problems.push('compiled, model, recording or runtime artifact');
  if (bytes > maxFileBytes) problems.push('file exceeds 10 MiB');
  return problems;
}

function contentProblems(text) {
  const found = [];
  const rules = [
    ['GitHub token', /\bgh[pousr]_[A-Za-z0-9]{36,}\b/g],
    ['GitHub fine-grained token', /\bgithub_pat_[A-Za-z0-9_]{60,}\b/g],
    ['Hugging Face token', /\bhf_[A-Za-z0-9]{30,}\b/g],
    ['private key', /-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----[\t \r\n]+[A-Za-z0-9+/=\r\n]{48,}/g],
    ['machine-specific user path', /\b[A-Za-z]:[\\/]+Users[\\/]+Z2SIX\b/gi],
  ];
  for (const [kind, pattern] of rules) {
    for (const match of text.matchAll(pattern)) {
      const line = 1 + (text.slice(0, match.index).match(/\n/g)?.length ?? 0);
      found.push({ line, kind });
    }
  }
  return found;
}

function withoutCode(text) {
  const lines = text.split(/\r?\n/);
  let fence = null;
  return lines.map(line => {
    const marker = line.match(/^\s{0,3}(`{3,}|~{3,})(.*)$/);
    if (marker) {
      if (!fence) fence = { character: marker[1][0], length: marker[1].length };
      else if (marker[1][0] === fence.character && marker[1].length >= fence.length && !marker[2].trim()) fence = null;
      return '';
    }
    return fence ? '' : line.replace(/(`+)[\s\S]*?\1/g, '');
  }).join('\n');
}

function markdownProblems(name, text, files) {
  const problems = [];
  const prose = withoutCode(text);
  const targets = [];
  for (const match of prose.matchAll(/!?\[[^\]\n]*\]\((<[^>]*>|[^)\n]*)\)/g)) targets.push([match[1], match.index]);
  for (const match of prose.matchAll(/^\s{0,3}\[[^\]\n]+\]:\s*(<[^>]*>|\S+)/gm)) targets.push([match[1], match.index]);
  for (const match of prose.matchAll(/\b(?:href|src)\s*=\s*["']([^"']+)["']/gi)) targets.push([match[1], match.index]);
  for (const [raw, index] of targets) {
    const destination = raw.startsWith('<') ? raw.slice(1, raw.indexOf('>')) : raw.trim().split(/\s+["']/)[0];
    if (!destination || destination.startsWith('#') || /^(?:https?:|mailto:|tel:|data:|\/\/)/i.test(destination)) continue;
    const line = 1 + (prose.slice(0, index).match(/\n/g)?.length ?? 0);
    if (/^(?:file:|[A-Za-z]:[\\/])/i.test(destination)) { problems.push({ line, kind: 'machine-local Markdown link' }); continue; }
    let target;
    try { target = decodeURIComponent(destination.split(/[?#]/)[0]).replaceAll('\\', '/'); }
    catch { problems.push({ line, kind: 'invalid Markdown link encoding' }); continue; }
    if (!target) continue;
    const normalized = posix.normalize(target.startsWith('/') ? target.slice(1) : posix.join(posix.dirname(name), target));
    const relative = normalized === '.' ? normalized : normalized.replace(/\/+$/, '');
    if (relative === '..' || relative.startsWith('../')) { problems.push({ line, kind: 'Markdown link escapes repository' }); continue; }
    if (relative !== '.' && !files.has(relative) && ![...files].some(file => file.startsWith(relative + '/')))
      problems.push({ line, kind: 'missing relative Markdown link target' });
  }
  return problems;
}

function runGit(args, options = {}) {
  const result = spawnSync(process.env.GIT_EXECUTABLE || 'git', ['-C', root, ...args], {
    windowsHide: true, encoding: 'utf8', maxBuffer: 128 * 1024 * 1024, ...options,
  });
  if (result.error || result.status !== 0) throw new Error('Git index could not be read. Ensure Git is available and this is a complete checkout.');
  return result.stdout;
}

function audit() {
  const gitRoot = runGit(['rev-parse', '--show-toplevel']).trim();
  if (realpathSync(gitRoot).toLowerCase() !== realpathSync(root).toLowerCase()) throw new Error('Run this checker from its own repository checkout.');
  const entries = runGit(['ls-files', '--stage', '-z']).split('\0').filter(Boolean).map(record => {
    const match = record.match(/^(\d{6}) ([a-f0-9]{40,64}) ([0-3])\t([\s\S]+)$/);
    if (!match || match[3] !== '0') throw new Error('Resolve unmerged or invalid index entries before auditing.');
    return { mode: match[1], oid: match[2], name: match[4] };
  });
  if (!entries.length) throw new Error('The index is empty. Stage the intended public files before auditing.');
  const metadata = runGit(['cat-file', '--batch-check=%(objectname) %(objecttype) %(objectsize)'], {
    input: entries.map(entry => entry.oid).join('\n') + '\n',
  }).trim().split('\n');
  if (metadata.length !== entries.length) throw new Error('Git blob metadata did not match the index.');
  const errors = [];
  const valid = [];
  let totalBytes = 0;
  for (let index = 0; index < entries.length; index++) {
    const entry = entries[index];
    const info = metadata[index].trim().match(/^([a-f0-9]{40,64}) (blob|commit|tree) (\d+)$/);
    if (!info || info[1] !== entry.oid) throw new Error('Invalid indexed-object metadata.');
    entry.bytes = Number(info[3]);
    let linked = false;
    try {
      let candidate = root;
      for (const part of entry.name.split('/')) { candidate = resolve(candidate, part); if (lstatSync(candidate).isSymbolicLink()) { linked = true; break; } }
    }
    catch (error) { if (error.code !== 'ENOENT') throw new Error('An indexed filesystem path could not be inspected.'); }
    const pathIssues = pathProblems(entry.name, entry.mode, entry.bytes, linked);
    if (info[2] !== 'blob') pathIssues.push('non-file Git object');
    for (const kind of pathIssues) errors.push({ file: entry.name, line: 1, kind });
    if (!pathIssues.length) { totalBytes += entry.bytes; valid.push(entry); }
  }
  // Prevent a malformed index from requesting unbounded process output. This
  // bound is independent of the 10 MiB per-file publication policy.
  if (totalBytes > 100 * 1024 * 1024) throw new Error('Indexed source exceeds the checker\'s 100 MiB batch limit; review the source inventory before publishing.');
  if (valid.length) {
    const batch = runGit(['cat-file', '--batch'], { encoding: null, input: valid.map(entry => entry.oid).join('\n') + '\n' });
    let offset = 0;
    const fileSet = new Set(entries.map(entry => entry.name));
    for (const entry of valid) {
      const headerEnd = batch.indexOf(10, offset);
      if (headerEnd < 0) throw new Error('Truncated indexed source stream.');
      const header = batch.subarray(offset, headerEnd).toString('ascii');
      if (header !== `${entry.oid} blob ${entry.bytes}`) throw new Error('Indexed source header mismatch.');
      const body = batch.subarray(headerEnd + 1, headerEnd + 1 + entry.bytes);
      if (body.length !== entry.bytes || batch[headerEnd + 1 + entry.bytes] !== 10) throw new Error('Truncated indexed source file.');
      offset = headerEnd + 2 + entry.bytes;
      if (assetExtensions.test(entry.name)) continue;
      let text;
      try { text = new TextDecoder('utf-8', { fatal: true }).decode(body); }
      catch { errors.push({ file: entry.name, line: 1, kind: 'unexpected binary or non-UTF-8 source' }); continue; }
      if (text.includes('\0')) { errors.push({ file: entry.name, line: 1, kind: 'unexpected binary source' }); continue; }
      for (const problem of contentProblems(text)) errors.push({ file: entry.name, ...problem });
      if (/\.md$/i.test(entry.name)) for (const problem of markdownProblems(entry.name, text, fileSet)) errors.push({ file: entry.name, ...problem });
    }
  }
  if (errors.length) {
    for (const error of errors) console.error(`FAIL ${error.file}:${error.line} [${error.kind}]`);
    console.error(`Public-source audit failed: ${errors.length} finding(s). Matched values are never printed.`);
    process.exitCode = 1;
  }
  else console.log(`PASS public-source audit: ${entries.length} indexed files, ${totalBytes} bytes; no generated/private artifacts, symlinks, known credential patterns, machine-specific user paths or broken relative Markdown file links detected.`);
}

function selfTest() {
  let checks = 0;
  const check = (condition, label) => { assert.ok(condition, label); checks++; };
  for (const name of ['LocalTalk/MainWindow.cs', 'FoxTransSource/native/vendor/miniaudio/miniaudio.h', 'Packaging/licenses/GPL-3.0.txt', 'Website/public/assets/saywhat-lockup.png', 'docs/models-and-runtimes.md'])
    check(pathProblems(name, '100644', 4_200_000).length === 0, `valid source/asset: ${name}`);
  for (const name of ['artifacts/app/SayWhat.exe', 'LocalTalk/obj/project.assets.json', '.wrangler/state.json', '.env', 'credentials.json', 'models/asr/model.onnx', 'people-photos/person.png', 'incoming-speaker-feedback.json', 'config.fast-chinese.jsonc', 'recordings/voice.wav', 'Packaging/tools/runtime.zip', '.ssh/id_ed25519'])
    check(pathProblems(name).length > 0, `private/generated file rejected: ${name}`);
  check(pathProblems('docs/link.md', '120000').includes('symlink or submodule'), 'symlink index mode rejected');
  for (const name of ['own-voice-runtime.json', 'own-voice-runtime.json.tmp-' + 'a'.repeat(32), 'SayWhat-support-20261030-120000.txt', 'saywhat-support-last-failure.txt'])
    check(pathProblems(name).includes('runtime preferences or credentials file'), `support/runtime data rejected: ${name}`);
  check(pathProblems('vendor/repository', '160000').includes('symlink or submodule'), 'submodule index mode rejected');
  check(pathProblems('docs/redirect.md', '100644', 1, true).includes('symlink or submodule'), 'filesystem link rejected');
  check(pathProblems('LocalTalk/too-large.cs', '100644', maxFileBytes + 1).includes('file exceeds 10 MiB'), 'oversized file rejected');
  check(pathProblems('../outside.cs').includes('invalid repository path'), 'path traversal rejected');
  const digest = 'a'.repeat(64);
  check(contentProblems(`var modelHash = "${digest}"; var apiKey = "fake-api-key"; // no credential`).length === 0, 'model hashes and fake API fixture text are not secrets');
  const synthetic = [
    ['ghp_' + 'A'.repeat(36), 'GitHub token'],
    ['github_pat_' + 'A'.repeat(70), 'GitHub fine-grained token'],
    ['hf_' + 'A'.repeat(32), 'Hugging Face token'],
    [['-----BEGIN ', 'PRIVATE KEY-----\n', 'A'.repeat(64), '\n-----END ', 'PRIVATE KEY-----'].join(''), 'private key'],
    [['C:', 'Users', 'Z2SIX', 'private'].join('\\'), 'machine-specific user path'],
  ];
  for (const [value, kind] of synthetic) {
    const problems = contentProblems('safe first line\n' + value);
    check(problems.some(problem => problem.kind === kind && problem.line === 2), `sensitive pattern reports only line/kind: ${kind}`);
    check(problems.every(problem => !('value' in problem) && !('match' in problem)), 'sensitive values are omitted from reports');
  }
  const files = new Set(['README.md', 'docs/guide.md', 'LocalTalk/MainWindow.cs', 'Website/public/assets/mark.png']);
  check(markdownProblems('README.md', '[Guide](docs/guide.md#section) [External](https://example.invalid/missing) [Here](#top)', files).length === 0, 'existing files, external links and anchors accepted');
  check(markdownProblems('docs/guide.md', '[Source](../LocalTalk/MainWindow.cs)', files).length === 0, 'relative parent links within repository accepted');
  check(markdownProblems('README.md', '[Source directory](LocalTalk/) [Same directory](LocalTalk)', files).length === 0, 'existing directory links work with or without a trailing slash');
  check(markdownProblems('README.md', '[Missing directory](missing-directory/)', files).some(problem => problem.kind === 'missing relative Markdown link target'), 'missing directory link remains rejected');
  check(markdownProblems('README.md', '[Missing](docs/absent.md)', files).some(problem => problem.kind === 'missing relative Markdown link target'), 'missing local Markdown target rejected');
  check(markdownProblems('README.md', '```md\n[Fixture](does-not-exist.md)\n```\n`[Inline](also-missing.md)`', files).length === 0, 'fenced and inline code links are ignored');
  check(markdownProblems('README.md', '````md\n```\n[Fixture](does-not-exist.md)\n```\n````', files).length === 0, 'shorter embedded fences do not close a longer code fence');
  check(markdownProblems('README.md', '[Outside](../other.md)', files).some(problem => problem.kind === 'Markdown link escapes repository'), 'escaping Markdown path rejected');
  check(markdownProblems('README.md', '![Logo](Website/public/assets/mark.png)\n[guide]: docs/guide.md', files).length === 0, 'image and reference definitions checked');
  check(markdownProblems('README.md', '<img src="Website/public/assets/mark.png">', files).length === 0, 'HTML image local target checked');
  console.log(`PASS ${checks} public-source audit self-checks; synthetic strings only, no Git data, network, models or user files.`);
}

try {
  if (process.argv.length === 3 && process.argv[2] === '--self-test') selfTest();
  else if (process.argv.length === 2) audit();
  else throw new Error('Use no arguments to audit the Git index, or --self-test for synthetic checks.');
}
catch (error) { console.error(`Public-source audit stopped: ${error.message}`); process.exitCode = 1; }
