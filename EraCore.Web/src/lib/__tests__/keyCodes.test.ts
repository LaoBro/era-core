import { describe, it, expect } from 'vitest';
import { virtualKeyFromCode, virtualKeyFromMouseButton } from '../keyCodes';

/**
 * 键码映射单测（spec-v3 M2b）。
 *
 * 锚点：Emuera 脚本侧写的就是 VK 码（`GETKEY(0x41)` = A、`GETKEY(0x1b)` = Esc），
 * 前端必须把**物理键位**（`event.code`）翻译成同一套编号，否则所有既有的
 * `GETKEY` 调用全部失配。这里逐类钉住翻译结果。
 */
describe('virtualKeyFromCode——物理键位 → Win32 VK', () => {
  it('字母 KeyA..KeyZ → 0x41..0x5a（= ASCII 大写）', () => {
    expect(virtualKeyFromCode('KeyA')).toBe(0x41);
    expect(virtualKeyFromCode('KeyZ')).toBe(0x5a);
    // 中间值抽查，防止写成偏移 1 的 off-by-one
    expect(virtualKeyFromCode('KeyM')).toBe(0x4d);
  });

  it('主键盘数字 Digit0..Digit9 → 0x30..0x39', () => {
    expect(virtualKeyFromCode('Digit0')).toBe(0x30);
    expect(virtualKeyFromCode('Digit9')).toBe(0x39);
  });

  it('小键盘数字 Numpad0..Numpad9 → 0x60..0x69（与主键盘不同号）', () => {
    expect(virtualKeyFromCode('Numpad0')).toBe(0x60);
    expect(virtualKeyFromCode('Numpad9')).toBe(0x69);
    expect(virtualKeyFromCode('Numpad9')).not.toBe(virtualKeyFromCode('Digit9'));
  });

  it('功能键 F1..F24 → 0x70..0x87', () => {
    expect(virtualKeyFromCode('F1')).toBe(0x70);
    expect(virtualKeyFromCode('F12')).toBe(0x7b);
    expect(virtualKeyFromCode('F24')).toBe(0x87);
  });

  it('越界功能键（F0 / F25）→ null', () => {
    expect(virtualKeyFromCode('F0')).toBeNull();
    expect(virtualKeyFromCode('F25')).toBeNull();
  });

  it('脚本常用的控制键', () => {
    expect(virtualKeyFromCode('Escape')).toBe(0x1b);   // GETKEY(0x1b) 打断
    expect(virtualKeyFromCode('Enter')).toBe(0x0d);
    expect(virtualKeyFromCode('Space')).toBe(0x20);
    expect(virtualKeyFromCode('Backspace')).toBe(0x08);
    expect(virtualKeyFromCode('Tab')).toBe(0x09);
  });

  it('方向键 → 0x25..0x28', () => {
    expect(virtualKeyFromCode('ArrowLeft')).toBe(0x25);
    expect(virtualKeyFromCode('ArrowUp')).toBe(0x26);
    expect(virtualKeyFromCode('ArrowRight')).toBe(0x27);
    expect(virtualKeyFromCode('ArrowDown')).toBe(0x28);
  });

  it('左右同码：Shift / Ctrl / Alt 不分侧（脚本只读一个编号）', () => {
    expect(virtualKeyFromCode('ShiftLeft')).toBe(0x10);
    expect(virtualKeyFromCode('ShiftRight')).toBe(0x10);
    expect(virtualKeyFromCode('ControlLeft')).toBe(0x11);
    expect(virtualKeyFromCode('ControlRight')).toBe(0x11);
    expect(virtualKeyFromCode('AltLeft')).toBe(0x12);
    expect(virtualKeyFromCode('AltRight')).toBe(0x12);
  });

  it('未识别的 code → null（不猜测，交由调用方跳过）', () => {
    expect(virtualKeyFromCode('IntlBackslash')).toBeNull();
    expect(virtualKeyFromCode('MetaLeft')).toBeNull();
    expect(virtualKeyFromCode('')).toBeNull();
  });
});

describe('virtualKeyFromMouseButton——鼠标键 → VK（统一走 keys 通道）', () => {
  it('左/中/右 → 0x01 / 0x04 / 0x02（VK_LBUTTON / MBUTTON / RBUTTON）', () => {
    expect(virtualKeyFromMouseButton(0)).toBe(0x01);
    expect(virtualKeyFromMouseButton(1)).toBe(0x04);
    expect(virtualKeyFromMouseButton(2)).toBe(0x02);
  });

  it('未知按键（如侧键 button=3/4）→ null', () => {
    expect(virtualKeyFromMouseButton(3)).toBeNull();
    expect(virtualKeyFromMouseButton(4)).toBeNull();
  });

  it('🔒 与键盘共用编号：鼠标键不得与字母/数字撞号', () => {
    const mouseKeys = [0, 1, 2].map(virtualKeyFromMouseButton);
    for (const vk of mouseKeys) {
      expect(vk!).toBeLessThan(0x30);   // 全部落在控制键区
    }
  });
});
