import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';

/**
 * 能力降级层（spec: .scratch/legacy-webview/spec.md）的源码契约断言。
 *
 * 机制无法在 happy-dom 里验证（弹不掉的声明 / 忽略的 gap 不能模拟），故对
 * 源码做静态契约，防止回归：
 *  - legacy-webview.css 中 `html.legacy-webview` 门控规则存在且把 --fx-rise 归零；
 *  - index.html 的内联探针 / 失败可见化脚本必须是纯 ES5（不经任何构建转换，落到
 *    旧机首行语法即崩 → 黑屏）。
 */

const cssSrc = readFileSync(`${process.cwd()}/src/styles/legacy-webview.css`, 'utf8');
const htmlSrc = readFileSync(`${process.cwd()}/index.html`, 'utf8');

describe('能力降级层源码契约', () => {
  it('--fx-rise 必须在 legacy-webview 门控下归零（避免旧机位移动画继续顶起）', () => {
    const rule = /html\.legacy-webview\s*\{([^}]*)\}/.exec(cssSrc);
    expect(rule, 'html.legacy-webview 门控规则缺失').toBeTruthy();
    expect(rule![1]).toContain('--fx-rise: 0px');
  });

  it('legacy-webview.css 的所有规则必须锚定在 html.legacy-webview 下（新机零影响）', () => {
    // 除注释外的每行声明，若以选择符开头，必先出现 html.legacy-webview
    const lines = cssSrc.split('\n');
    for (const line of lines) {
      const trimmed = line.trim();
      if (!trimmed) continue;
      if (trimmed.startsWith('/*') || trimmed.startsWith('*') || trimmed.startsWith('*/')) continue;
      if (trimmed.endsWith('{') || trimmed.endsWith('}')) {
        expect(
          trimmed.startsWith('html.legacy-webview'),
          `门控外出现规则行：${trimmed}`,
        ).toBe(true);
      }
    }
  });
});

describe('index.html 内联探针 ES5 校验', () => {
  const inlineScripts = [...htmlSrc.matchAll(/<script>\s*([\s\S]*?)\s*<\/script>/g)].map(
    (m) => m[1],
  );
  it('必须存在能力降级探针 + 失败可见化两个内联脚本', () => {
    expect(inlineScripts.length).toBeGreaterThanOrEqual(2);
    expect(inlineScripts.some((s) => s.includes('legacy-webview'))).toBe(true);
    expect(inlineScripts.some((s) => s.includes('失败可见化') || s.includes('脚本未加载'))).toBe(
      true,
    );
  });

  const ES6 = [
    ['箭头函数', /=>/],
    ['let/const 声明', /\b(?:let|const)\s/],
    ['模板字符串', /`/],
    ['可选链/空值合并', /\?\.|\?\?/],
    ['解构', /\b(?:const|let)\s+\{[^}]*\}\s*=/],
    ['class 关键字', /\bclass\s+\w/],
  ] as const;

  it.each(ES6)('内联脚本不含%s（纯 ES5，不经构建转换）', (_label, pattern) => {
    for (const src of inlineScripts) {
      expect(src, '探针脚本非纯 ES5').not.toMatch(pattern);
    }
  });
});