/**
 * 浏览器键盘事件 → Win32 虚拟键码（VK）——纯函数，便于单测。
 *
 * ## 为什么用 `event.code` 而不是 `event.keyCode`
 *
 * `keyCode` 受键盘布局影响（AZERTY 上 `KeyA` 位置的键不是 A），且已废弃；
 * `code` 是**物理键位**标识（`KeyA` / `Digit1` / `ArrowLeft`），与布局无关。
 * Emuera 脚本侧写的是 VK 码（`GETKEY(0x41)` = A），故需要这张映射。
 *
 * ## 范围
 *
 * 只覆盖 Emuera 脚本实际会读的键：控制键、方向键、功能键、字母/数字/小键盘。
 * 未识别的 `code` 返回 `null` —— 调用方跳过，不猜测。
 *
 * spec-v3 §5.1 / M2b。
 */

/** 无法用规则推导的特殊键。 */
const VK_SPECIAL: Record<string, number> = {
  Backspace: 0x08,
  Tab: 0x09,
  Enter: 0x0d,
  NumpadEnter: 0x0d,
  ShiftLeft: 0x10,
  ShiftRight: 0x10,
  ControlLeft: 0x11,
  ControlRight: 0x11,
  AltLeft: 0x12,
  AltRight: 0x12,
  Escape: 0x1b,
  Space: 0x20,
  PageUp: 0x21,
  PageDown: 0x22,
  End: 0x23,
  Home: 0x24,
  ArrowLeft: 0x25,
  ArrowUp: 0x26,
  ArrowRight: 0x27,
  ArrowDown: 0x28,
  Insert: 0x2d,
  Delete: 0x2e,
  NumLock: 0x90,
  ScrollLock: 0x91,
  CapsLock: 0x14,
  ContextMenu: 0x5d,
};

/**
 * 把 `KeyboardEvent.code` 映射为 Win32 VK 码。
 * @returns VK 码（0–255）；未识别返回 `null`。
 */
export function virtualKeyFromCode(code: string): number | null {
  const special = VK_SPECIAL[code];
  if (special !== undefined) return special;

  // 主键盘数字 0-9 → VK 0x30-0x39
  const digit = /^Digit(\d)$/.exec(code);
  if (digit) return 0x30 + Number(digit[1]);

  // 字母 A-Z → VK 0x41-0x5a（= ASCII 大写）
  const letter = /^Key([A-Z])$/.exec(code);
  if (letter) return 0x41 + (letter[1].charCodeAt(0) - 0x41);

  // 小键盘数字 0-9 → VK 0x60-0x69
  const numpad = /^Numpad(\d)$/.exec(code);
  if (numpad) return 0x60 + Number(numpad[1]);

  // 功能键 F1-F24 → VK 0x70-0x87
  const fn = /^F(\d{1,2})$/.exec(code);
  if (fn) {
    const n = Number(fn[1]);
    if (n >= 1 && n <= 24) return 0x6f + n;
  }

  return null;
}

/**
 * 鼠标键 `MouseEvent.button` → Win32 VK 码。
 *
 * 之所以不必为鼠标单开协议字段：脚本侧鼠标键就是读 `GETKEY(1)` / `GETKEY(2)` /
 * `GETKEY(4)`，与键盘共用同一套 VK 编号。故统一走 `keys` 通道即可
 * （spec-v3 §5.1「删掉 button 字段」）。
 *
 * @returns VK 码；未知按键返回 `null`。
 */
export function virtualKeyFromMouseButton(button: number): number | null {
  switch (button) {
    case 0: return 0x01;  // VK_LBUTTON
    case 1: return 0x04;  // VK_MBUTTON
    case 2: return 0x02;  // VK_RBUTTON
    default: return null;
  }
}
