// @vitest-environment happy-dom
import { describe, it, expect } from 'vitest';
import { isTypingTarget } from '../useKeyMouseInput';

/**
 * `useKeyMouseInput` 里唯一可单测的纯函数：`isTypingTarget`。
 *
 * （监听器本体依赖 window/document 与 connection store，留给集成层；这里只钉住
 * 输入框守卫的判定 —— 它决定了「玩家打字会不会被误当成游戏内按键」。）
 *
 * spec-v3 M2b：玩家在 InputBar 打字时不该让 `GETKEY(0x41)` 变成 1。
 */
describe('isTypingTarget——焦点是否落在可输入元素', () => {
  it('输入框 / 文本域 / 下拉 → true', () => {
    expect(isTypingTarget(document.createElement('input'))).toBe(true);
    expect(isTypingTarget(document.createElement('textarea'))).toBe(true);
    expect(isTypingTarget(document.createElement('select'))).toBe(true);
  });

  it('contentEditable → true', () => {
    const div = document.createElement('div');
    div.contentEditable = 'true';
    expect(isTypingTarget(div)).toBe(true);
  });

  it('普通元素 → false（含 button —— 焦点在按钮上不算打字）', () => {
    expect(isTypingTarget(document.createElement('div'))).toBe(false);
    expect(isTypingTarget(document.createElement('button'))).toBe(false);
    expect(isTypingTarget(document.createElement('span'))).toBe(false);
  });

  it('null（无 activeElement）→ false（不抛）', () => {
    expect(isTypingTarget(null)).toBe(false);
  });

  it('🔒 标签名大小写无关（HTML 里 createElement 返回大写 tagName）', () => {
    const el = document.createElement('input');
    expect(el.tagName).toBe('INPUT');
    expect(isTypingTarget(el)).toBe(true);
  });
});
