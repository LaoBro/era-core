#!/usr/bin/env node
/**
 * gen-legacy-gap.mjs — 生成 legacy-webview.css 的 flex gap 兜底段（ID3）。
 *
 * 原理：扫描 src/**​*.vue 里 `<style>` 块，找出「display:flex + gap>0」的容器，
 * 按 flex-direction 方向把 gap 翻译成 `.legacy-webview` 门控的相邻兄弟 margin
 * （row→margin-left；column→margin-top；wrap / gap:0 / 非纯类选择器跳过）。
 * 把结果写进 legacy-webview.css 的 `<generated legacy-gap:begin/end>` 标记之间。
 *
 * 目的：旧 WebView 静默忽略 flex gap，用这个降级兜底；新机因 .legacy-webview
 * 不匹配而零影响。组件里改 gap 后重跑本脚本即可防漂移。
 *
 * 用法：node scripts/gen-legacy-gap.mjs
 */
import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const srcDir = fileURLToPath(new URL('../src/', import.meta.url));
const cssPath = fileURLToPath(new URL('../src/styles/legacy-webview.css', import.meta.url));

const BEGIN = '/* <generated legacy-gap:begin> DO NOT EDIT */';
const END = '/* <generated legacy-gap:end> */';

/** 递归收集目录下所有文件绝对路径。 */
function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) out = walk(p, out);
    else out.push(p);
  }
  return out;
}

/** 从一段 CSS 文本提取「selector + body」的扁平规则（忽略含内层花括号的 @media/@keyframes 外层）。 */
function* cssRules(css) {
  // 先剥块注释——否则规则上方紧邻的 `/* ... */` 会被 `[^{}]*` 并入 selector，导致误判非纯类选择器
  css = css.replace(/\/\*[\s\S]*?\*\//g, '');
  const re = /([^{}]*)\{([^{}]*)\}/g;
  let m;
  while ((m = re.exec(css)) !== null) {
    const selector = m[1].trim();
    const body = m[2].trim();
    if (!selector || !body) continue;
    yield { selector, body };
  }
}

function gapValue(body) {
  const g = /\bgap\s*:\s*([^;]+)/.exec(body);
  return g ? g[1].trim() : null;
}

/** 是否为可安全前缀的纯类选择器串（逗号分隔的全是 `.x` / `#x` 简单类）。 */
function isPlainSelector(selector) {
  return selector.split(',').every(
    (s) => /^(\.|#)[A-Za-z0-9_-]+$/.test(s.trim()) || /^[A-Za-z_-][\w-]*$/.test(s.trim()),
  );
}

function buildRule(selector, value, marginProp) {
  const prefixed = selector
    .split(',')
    .map((s) => `html.legacy-webview ${s.trim()}`)
    .join(',\n');
  return `${prefixed} > * + * { ${marginProp}: ${value}; }`;
}

/** 主流程：收集每文件的 flex-gap 容器 → 生成按文件分组的规则块。 */
function generate() {
  const perFile = new Map(); // basename → [{selector, value, marginProp}]

  for (const file of walk(srcDir)) {
    if (!file.endsWith('.vue')) continue;
    const raw = readFileSync(file, 'utf8');
    const styles = [...raw.matchAll(/<style\b[^>]*>([\s\S]*?)<\/style>/gi)].map((m) => m[1]);
    if (styles.length === 0) continue;

    const rules = [];
    for (const css of styles) {
      for (const { selector, body } of cssRules(css)) {
        const value = gapValue(body);
        if (!value || value === '0') continue;           // gap:0 无意义
        if (!/display\s*:\s*flex\b/.test(body)) continue; // 非 flex 容器
        if (/flex-wrap\s*:\s*wrap\b/.test(body)) continue; // wrap 需四向，跳过
        if (!isPlainSelector(selector)) continue;         // 伪类/深层/:deep 跳过
        const marginProp = /flex-direction\s*:\s*column\b/.test(body) ? 'margin-top' : 'margin-left';
        rules.push({ selector: selector.replace(/\s+/g, ' '), value, marginProp });
      }
    }
    if (rules.length) perFile.set(relative(srcDir, file), rules);
  }

  // 按文件名字典序 → 文件内按 selector 排序，保证输出幂等
  const lines = [];
  for (const file of [...perFile.keys()].sort()) {
    lines.push(`\n/* ${file} */`);
    for (const r of perFile.get(file).sort((a, b) => a.selector.localeCompare(b.selector))) {
      lines.push(buildRule(r.selector, r.value, r.marginProp));
    }
  }

  return lines.join('\n').trim();
}

function patch(css, generated) {
  const bi = css.indexOf(BEGIN);
  const ei = css.indexOf(END);
  if (bi < 0 || ei < 0 || ei <= bi) {
    throw new Error(`legacy-webview.css 缺少生成区标记 (${BEGIN} … ${END})`);
  }
  return css.slice(0, bi + BEGIN.length) + '\n' + generated + '\n' + css.slice(ei);
}

const css = readFileSync(cssPath, 'utf8');
const generated = generate();
writeFileSync(cssPath, patch(css, generated), 'utf8');
console.log(`[gen-legacy-gap] 已写入 ${generated.split('\n').filter((l) => l.trim().startsWith('html.legacy-webview')).length} 条 gap 兜底规则到 legacy-webview.css`);