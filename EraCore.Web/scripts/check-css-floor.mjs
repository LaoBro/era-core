#!/usr/bin/env node
/**
 * check-css-floor.mjs — CSS 地板护栏（ID6，可选）：扫源码列出「超出 WebView 地板
 * （Chromium 61）的 CSS 特性 + 处数」，打印清单但不 fail。用于维护 legacy 双声明
 * 期（ID4）与 gap 期（ID3）清单，避免"静默缺口"没记录。
 *
 * 原理：Chromium 61 不支持/降格的特性只在旧机静默失效（构建不报错、新机不复现）。
 * 这里在改动后跑一遍，把缺口变成可见清单。默认不接线到构建，warning 性质。
 *
 * 用法：node scripts/check-css-floor.mjs [--dir=src]
 */
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const defaultDir = resolve(scriptDir, '..', 'src');
const arg = process.argv.find((a) => a.startsWith('--dir='));
const rootDir = arg ? resolve(process.cwd(), arg.slice('--dir='.length)) : defaultDir;

/** [label, regex] —— 命中处计入清单（regex 需带 g，按处数统计）。 */
const FEATURES = [
  ['gap:（flex 容器）需 gen-legacy-gap 兜底', /\bgap\s*:/g],
  ['color-mix() 已/需满强度兜底', /color-mix\s*\(/g],
  ['env(safe-area-*) 已/需固定值前插', /env\s*\(\s*safe-area-/g],
  ['[0-9]dvh 需 vh 前插', /\b\d+dvh\b/g],
  ['overflow: clip 不可用（61 起 90+）', /overflow\s*:\s*clip/g],
  [':focus-visible（键盘焦点，本次接受）', /:focus-visible/g],
];

function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) out = walk(p, out);
    else if (/\.(css|vue)$/.test(p)) out.push(p);
  }
  return out;
}

const counts = {}; // label -> total
const byFile = {}; // file -> {label: n}
for (const file of walk(rootDir)) {
  const src = readFileSync(file, 'utf8');
  for (const [label, re] of FEATURES) {
    const n = (src.match(re) || []).length;
    if (n > 0) {
      counts[label] = (counts[label] || 0) + n;
      (byFile[file] ??= {})[label] = (byFile[file][label] || 0) + n;
    }
  }
}

console.log(`[check-css-floor] CSS 地板（chrome61）缺口清单 ${rootDir}\n`);
const total = Object.entries(counts);
if (total.length === 0) {
  console.log('  未发现超出地板的 CSS 特性——干净。');
  process.exit(0);
}
total.sort((a, b) => b[1] - a[1]).forEach(([label, n]) => {
  console.log(`  ${String(n).padStart(3)}  ${label}`);
});
console.log('\n  按文件：');
for (const [file, l] of Object.entries(byFile).sort()) {
  const items = Object.entries(l).map(([k, v]) => `${k.replace(/^.*?(gap|color-mix|env|dvh|clip|focus-visible).*$/, '$1')}×${v}`).join(', ');
  console.log(`    ${file.replace(rootDir + '\\', '')}  ${items}`);
}
process.exit(0);