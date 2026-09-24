/**
 * Vitest 全局前置（vite.config.ts → `test.setupFiles`）——补一个环境缺口，不改产品行为。
 *
 * 缺口：Node 22+ 自带 `localStorage` 全局，未给 `--localstorage-file` 时读出来是 `undefined`
 * （Node 26 上还会打一条 ExperimentalWarning）。而 vitest 的 `populateGlobal` 只在
 * 「globalThis 上还没有同名 key」时才把测试环境（happy-dom）的那份装进来——
 * 其 `getWindowKeys` 的判据是 `if (k in global) return keysArray.includes(k)`，
 * 而 `localStorage` 不在白名单 `KEYS` 里。于是即便加了 `// @vitest-environment happy-dom`，
 * 文件里的 `localStorage` 仍是 undefined：`localStorage.clear()` 直接抛
 * `TypeError: Cannot read properties of undefined`（见 App.menuPrewarm.test.ts）。
 * 对照：`sessionStorage` 不在 Node 全局里，所以它被正常装进来了。
 *
 * 这里按 `Storage` 契约补一个内存实现。只在**当前不可用**时补——
 * 环境自己提供了能用的实现（真实浏览器 / 未来 vitest 修好之后）就原样不动；
 * 需要精确控制存储内容的用例仍可 `vi.stubGlobal('localStorage', ...)` 覆盖。
 *
 * 每个测试文件各自跑一次 setup，故各文件的存储天然隔离；文件内清理由用例
 * 自己的 `beforeEach`（如 App.menuPrewarm.test.ts 的 `localStorage.clear()`）负责。
 */

/** 内存版 localStorage——只实现 `Storage` 契约，够测试用（无配额、无事件）。 */
function createMemoryStorage(): Storage {
  const map = new Map<string, string>();
  return {
    get length() {
      return map.size;
    },
    clear() {
      map.clear();
    },
    getItem(key: string) {
      return map.has(key) ? map.get(key)! : null;
    },
    key(index: number) {
      return Array.from(map.keys())[index] ?? null;
    },
    removeItem(key: string) {
      map.delete(key);
    },
    setItem(key: string, value: string) {
      map.set(String(key), String(value));
    },
  };
}

let localStorageUsable = false;
try {
  localStorageUsable = typeof globalThis.localStorage?.getItem === 'function';
} catch {
  // 访问被禁（隐私模式等）——视为不可用，走下面的兜底
  localStorageUsable = false;
}

if (!localStorageUsable) {
  Object.defineProperty(globalThis, 'localStorage', {
    value: createMemoryStorage(),
    writable: true,
    configurable: true,
    enumerable: false,
  });
}