#!/usr/bin/env node
/**
 * check-legacy-syntax.mjs — JS 地板护栏（ID6）：扫 Vite 产物里的 JS，
 * 命中「esbuild 以 target=chrome61 既不降级也不报错、会原样/半原样留在产物里、
 * 一落到旧 WebView 就是首行 SyntaxError」的语法 → 打印命中位置并 exit 1。
 *
 * 实测 esbuild chrome61（2026-09 验证）：
 *   - `(?<name>` / `(?<=`：原样透传          → Chrome 61 不识别命名组/后顾（62/64+）→ SyntaxError
 *   - `\p{Letter}`：原样透传                 → Chrome 61 不识别 Unicode 属性转义（64+）→ SyntaxError
 *   - `/re/s`：转成 new RegExp(str, "s")     → 61 的 RegExp 构造器不支持 dotAll 标志（62+）→ SyntaxError
 *   - `123n`：降级成 `BigInt("123")`         → 不报语法错，但在 61 上 BigInt 未定义 → 仅作兜底检查
 *
 * 用法：node scripts/check-legacy-syntax.mjs [--dir=dist]
 *   默认扫 <script目录>/../dist（即 EraCore.Web/dist）。
 *   由 build/VueBuild.targets 在 BuildVueFrontend 后门住 CLI 与 MAUI 两条路径；
 *   也可 `npm run check:legacy` 手跑。
 */
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const defaultDir = resolve(scriptDir, '..', 'dist');

const arg = process.argv.find((a) => a.startsWith('--dir='));
const targetDir = arg ? resolve(process.cwd(), arg.slice('--dir='.length)) : defaultDir;

/** [label, regex] —— 命中即违例。 */
const PATTERNS = [
  ['命名捕获组 / 后顾断言', /\(\?</g],
  ['Unicode 属性转义', /(?<!\\)\\(?:[pP])\{/g],
  ['dotAll 标志（new RegExp(…,"…s…")）', /new\s+RegExp\(\s*[^,)\]]+,\s*(["'])[a-z]*s[a-z]*\1\s*\)/g],
  ['BigInt 字面量', /\b\d+n\b/g],
];

function walk(dir, out = []) {
  let entries;
  try {
    entries = readdirSync(dir);
  } catch {
    return out; // 目录不存在（如未 build）→ 不视为违例
  }
  for (const name of entries) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) out = walk(p, out);
    else if (p.endsWith('.js')) out.push(p);
  }
  return out;
}

const files = walk(targetDir);
const violations = [];
for (const file of files) {
  const src = readFileSync(file, 'utf8');
  for (const [label, re] of PATTERNS) {
    re.lastIndex = 0;
    let m;
    while ((m = re.exec(src)) !== null) {
      const idx = m.index;
      const start = Math.max(0, idx - 40);
      const end = Math.min(src.length, idx + m[0].length + 40);
      const ctx = src.slice(start, end).replace(/\n/g, '⏎');
      violations.push(
        `${file}:${idx}  [${label}]  match=${JSON.stringify(m[0])}\n    …${ctx}…`,
      );
      re.lastIndex = idx + 1; // 避免重叠漏检
    }
  }
}

if (violations.length) {
  console.error(`[check-legacy-syntax] ✗ 命中 ${violations.length} 处超出 WebView 地板（chrome61）的 JS 语法，可能旧机首行 SyntaxError → 黑屏：\n`);
  for (const v of violations) console.error('  ' + v);
  console.error(`\n[check-legacy-syntax] 请改写这些表达式（用 ES5 等价写法 / 避免超出地板的 Regex 特性）。`);
  process.exit(1);
}

console.log(`[check-legacy-syntax] ✓ ${files.length} 个 JS 产物通过 chrome61 语法护栏：${targetDir}`);
process.exit(0);