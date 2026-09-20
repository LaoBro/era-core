import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useGameStore } from '../game';

/**
 * ADR-0030：全局设置 store 层单测（HTTP 模式）。
 *
 * 覆盖：
 * - applySettingsUpdate：13 键统一应用（含 agentLogEnabled 字符串/bool 两种形态、非法值忽略）
 * - fetchSettings：GET /settings → 权威值写入
 * - updateSettingsHttp：PUT 单键 → 响应回写；非 200 返回 false
 * - resetSettingsHttp：DELETE → 全回默认
 */

function makeLocalStorage(): Storage {
  const map = new Map<string, string>();
  return {
    getItem: (k: string) => (map.has(k) ? map.get(k)! : null),
    setItem: (k: string, v: string) => {
      map.set(k, v);
    },
    removeItem: (k: string) => {
      map.delete(k);
    },
    clear: () => {
      map.clear();
    },
    key: (i: number) => Array.from(map.keys())[i] ?? null,
    get length() {
      return map.size;
    },
  };
}

describe('全局设置 store（ADR-0030）', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.stubGlobal('localStorage', makeLocalStorage());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('applySettingsUpdate 应用 13 键 + agentLog 字符串形态', () => {
    const game = useGameStore();
    game.applySettingsUpdate({
      maxLog: 1234,
      agentLogEnabled: 'true',
      displayReportOverride: 'off',
      lazyFunctionLoad: 'on',
      parallelErbLoad: 'off',
      sweepGatePolicy: 'forceoff',
      safReadDegree: '4',
      maxParallelErbFiles: '16',
      dimLockPolicy: 'nolock',
    });
    expect(game.maxLog).toBe(1234);
    expect(game.agentLogEnabled).toBe(true);
    expect(game.displayReportOverride).toBe('off');
    expect(game.perfOverrides.lazyFunctionLoad).toBe('on');
    expect(game.perfOverrides.parallelErbLoad).toBe('off');
    expect(game.sweepGatePolicy).toBe('forceoff');
    expect(game.safReadDegree).toBe('4');
    expect(game.maxParallelErbFiles).toBe('16');
    expect(game.dimLockPolicy).toBe('nolock');
  });

  it('applySettingsUpdate 忽略非法值（MAUI config 消息同款校验）', () => {
    const game = useGameStore();
    game.applySettingsUpdate({
      agentLogEnabled: 'maybe',
      displayReportOverride: 'bogus',
      sweepGatePolicy: 'forceanything',
      safReadDegree: 'abc', // 非数值字符串非法（'999' 匹配 ^\d+$ 会通过，C# 侧才钳制 1–64）
      maxParallelErbFiles: '32',
      dimLockPolicy: 'lock',
    } as Record<string, unknown>);
    // 非法值不覆盖默认
    expect(game.agentLogEnabled).toBe(false);
    expect(game.displayReportOverride).toBe('off');
    expect(game.sweepGatePolicy).toBe('auto');
    expect(game.safReadDegree).toBe('auto');
    expect(game.maxParallelErbFiles).toBe('auto');
    expect(game.dimLockPolicy).toBe('auto');
  });

  it('fetchSettings GET /settings 写入权威值', async () => {
    const game = useGameStore();
    const fetchMock = vi.fn().mockResolvedValue({
      status: 200,
      json: () => Promise.resolve({ displayReportOverride: 'on', sweepGatePolicy: 'forceoff' }),
    });
    vi.stubGlobal('fetch', fetchMock);

    await game.fetchSettings('http://localhost:8080');

    expect(fetchMock).toHaveBeenCalledWith('http://localhost:8080/settings');
    expect(game.displayReportOverride).toBe('on');
    expect(game.sweepGatePolicy).toBe('forceoff');
  });

  it('updateSettingsHttp PUT 单键并回写响应', async () => {
    const game = useGameStore();
    const fetchMock = vi.fn().mockResolvedValue({
      status: 200,
      json: () => Promise.resolve({ safReadDegree: '8' }),
    });
    vi.stubGlobal('fetch', fetchMock);

    const ok = await game.updateSettingsHttp('http://localhost:8080', 'safReadDegree', '8');

    expect(ok).toBe(true);
    expect(fetchMock).toHaveBeenCalledWith(
      'http://localhost:8080/settings',
      expect.objectContaining({ method: 'PUT' }),
    );
    expect(game.safReadDegree).toBe('8');
  });

  it('updateSettingsHttp 非 200 返回 false 不写状态', async () => {
    const game = useGameStore();
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ status: 400, json: () => Promise.resolve({}) }));

    const ok = await game.updateSettingsHttp('http://localhost:8080', 'dimLockPolicy', 'lock');

    expect(ok).toBe(false);
    expect(game.dimLockPolicy).toBe('auto');
  });

  it('resetSettingsHttp DELETE 回默认', async () => {
    const game = useGameStore();
    game.applySettingsUpdate({ sweepGatePolicy: 'forceoff' });
    const fetchMock = vi.fn().mockResolvedValue({
      status: 200,
      json: () => Promise.resolve({ sweepGatePolicy: 'auto' }),
    });
    vi.stubGlobal('fetch', fetchMock);

    const ok = await game.resetSettingsHttp('http://localhost:8080');

    expect(ok).toBe(true);
    expect(fetchMock).toHaveBeenCalledWith(
      'http://localhost:8080/settings',
      expect.objectContaining({ method: 'DELETE' }),
    );
    expect(game.sweepGatePolicy).toBe('auto');
  });
});
