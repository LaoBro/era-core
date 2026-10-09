import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { startGameStatusMonitor } from '../useGameStatusMonitor';
import { useGameStore } from '../../stores/game';

/**
 * useGameStatusMonitor 单测——前端静默监控（替代 C# 死循环检测）。
 *
 * 语义：
 * - 非 MAUI 环境 → no-op（HTTP 模式有 WS 连接语义，C# 无此消息处理）
 * - 无活跃游戏 → 不探测 + 清空提示
 * - 服务端停稳在等输入 / 等定时器（且未提交输入）→ 不探测 + 清空提示
 *   （issue 07：判据改读 `awaiting.input` / `awaiting.wait`，不再读 `state === 'WaitInput'`）
 * - 已提交输入（慢回合计算中，state 停留旧 WaitInput）→ 视为"忙"，
 *   静默超时（5s）无帧则投递 getGameThreadStatus 探测一次
 * - 非 WaitInput 态（Running）静默超时 → 探测
 * - 已有提示 → 不重复探测（新 turn 到达会自动清空并重新计时）
 */

/** 模拟 MAUI Android file: 协议 window + chrome.webview 记录 postMessage。 */
function stubMauiWindow(): ReturnType<typeof vi.fn> {
  const postSpy = vi.fn();
  vi.stubGlobal('window', {
    location: { protocol: 'file:', hostname: '' },
    chrome: { webview: { postMessage: postSpy } },
  } as unknown as Window & typeof globalThis);
  return postSpy;
}

describe('useGameStatusMonitor', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('非 MAUI 环境 → no-op，不投递探测', () => {
    const postSpy = stubMauiWindow();
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'localhost' },
      chrome: { webview: { postMessage: postSpy } },
    } as unknown as Window & typeof globalThis);

    const game = useGameStore();
    game.gameDir = 'D:/game';
    game.applyTurn(JSON.stringify({ state: 'Running', needValue: false, generation: 0 }));
    game.lastActivityAt = Date.now() - 60000;

    startGameStatusMonitor();
    vi.advanceTimersByTime(10000);

    expect(postSpy).not.toHaveBeenCalled();
  });

  it('游戏活跃 + 静默超时 → 投递 getGameThreadStatus', () => {
    const postSpy = stubMauiWindow();
    const game = useGameStore();
    game.gameDir = 'D:/game';
    game.applyTurn(JSON.stringify({ state: 'Running', needValue: false, generation: 0 }));
    game.lastActivityAt = Date.now() - 10000;

    startGameStatusMonitor();
    vi.advanceTimersByTime(2000);

    expect(postSpy).toHaveBeenCalledWith(JSON.stringify({ type: 'getGameThreadStatus' }));
  });

  it('WaitInput 等待输入 → 不探测且清空已有提示', () => {
    const postSpy = stubMauiWindow();
    const game = useGameStore();
    game.gameDir = 'D:/game';
    // issue 07：判据改读 awaiting.input ⇒ 夹具必须带 inputType（"等玩家输入"的 wire 形态）
    game.applyTurn(JSON.stringify({ state: 'WaitInput', inputType: 'AnyKey', needValue: false, generation: 0 }));
    game.lastActivityAt = Date.now() - 60000;
    game.setGameStatusHint('running');

    startGameStatusMonitor();
    vi.advanceTimersByTime(10000);

    expect(postSpy).not.toHaveBeenCalled();
    expect(game.gameStatusHint).toBeNull();
  });

  it('定时等待（只有 wait 维度）→ 不探测且清空已有提示（服务端计时器会自行推进）', () => {
    const postSpy = stubMauiWindow();
    const game = useGameStore();
    game.gameDir = 'D:/game';
    game.applyTurn(JSON.stringify({
      state: 'WaitInput',
      generation: 0,
      protocolVersion: 12,
      diff: null,
      awaiting: { wait: { durationMs: 8000 } },
    }));
    game.lastActivityAt = Date.now() - 60000;
    game.setGameStatusHint('running');

    startGameStatusMonitor();
    vi.advanceTimersByTime(10000);

    expect(postSpy).not.toHaveBeenCalled();
    expect(game.gameStatusHint).toBeNull();
  });

  it('零时长让路帧（durationMs=0）同样视为停稳 → 不探测', () => {
    const postSpy = stubMauiWindow();
    const game = useGameStore();
    game.gameDir = 'D:/game';
    game.applyTurn(JSON.stringify({
      state: 'WaitInput',
      generation: 0,
      protocolVersion: 12,
      diff: null,
      awaiting: { wait: { durationMs: 0 } },
    }));
    game.lastActivityAt = Date.now() - 60000;

    startGameStatusMonitor();
    vi.advanceTimersByTime(10000);

    expect(postSpy).not.toHaveBeenCalled();
  });

  it('提交输入后静默超时（前端 state 仍为旧 WaitInput）→ 探测线程存活', () => {
    const postSpy = stubMauiWindow();
    const game = useGameStore();
    game.gameDir = 'D:/game';
    // 上一回合：等待输入
    // issue 07：判据改读 awaiting.input ⇒ 夹具必须带 inputType（"等玩家输入"的 wire 形态）
    game.applyTurn(JSON.stringify({ state: 'WaitInput', inputType: 'AnyKey', needValue: false, generation: 0 }));
    // 用户提交输入——慢回合计算期间 C# 不产帧，state 停留在 WaitInput
    game.markInputSubmitted();
    game.lastActivityAt = Date.now() - 10000;

    startGameStatusMonitor();
    vi.advanceTimersByTime(2000);

    expect(postSpy).toHaveBeenCalledWith(JSON.stringify({ type: 'getGameThreadStatus' }));
  });

  it('提交输入后新 turn 到达 → 解除忙态，恢复等待判定不再探测', () => {
    const postSpy = stubMauiWindow();
    const game = useGameStore();
    game.gameDir = 'D:/game';
    // issue 07：判据改读 awaiting.input ⇒ 夹具必须带 inputType（"等玩家输入"的 wire 形态）
    game.applyTurn(JSON.stringify({ state: 'WaitInput', inputType: 'AnyKey', needValue: false, generation: 0 }));
    game.markInputSubmitted();
    // 慢回合处理完成，新 turn 到达——inputSubmittedAt 清空，状态恢复 WaitInput
    game.applyTurn(JSON.stringify({ state: 'WaitInput', inputType: 'AnyKey', needValue: false, generation: 1 }));
    game.lastActivityAt = Date.now() - 60000;

    startGameStatusMonitor();
    vi.advanceTimersByTime(10000);

    expect(postSpy).not.toHaveBeenCalled();
    expect(game.gameStatusHint).toBeNull();
  });

  it('无活跃游戏（gameDir 为空）→ 不探测且清空已有提示', () => {
    const postSpy = stubMauiWindow();
    const game = useGameStore();
    game.applyTurn(JSON.stringify({ state: 'Running', needValue: false, generation: 0 }));
    game.lastActivityAt = Date.now() - 60000;
    game.setGameStatusHint('stopped');

    startGameStatusMonitor();
    vi.advanceTimersByTime(10000);

    expect(postSpy).not.toHaveBeenCalled();
    expect(game.gameStatusHint).toBeNull();
  });

  it('已有提示 → 不重复探测', () => {
    const postSpy = stubMauiWindow();
    const game = useGameStore();
    game.gameDir = 'D:/game';
    game.applyTurn(JSON.stringify({ state: 'Running', needValue: false, generation: 0 }));
    game.lastActivityAt = Date.now() - 60000;
    game.setGameStatusHint('running');

    startGameStatusMonitor();
    vi.advanceTimersByTime(10000);

    expect(postSpy).not.toHaveBeenCalled();
    expect(game.gameStatusHint).toBe('running');
  });

  it('静默超时前有活动 → 不探测；超时后探测，新 turn 到达后重新计时', () => {
    const postSpy = stubMauiWindow();
    const game = useGameStore();
    game.gameDir = 'D:/game';
    game.applyTurn(JSON.stringify({ state: 'Running', needValue: false, generation: 0 }));

    startGameStatusMonitor();
    vi.advanceTimersByTime(4000);
    expect(postSpy).not.toHaveBeenCalled();

    // 越过 5s 静默阈值 → 探测
    vi.advanceTimersByTime(2000);
    expect(postSpy).toHaveBeenCalledTimes(1);

    // 新 turn 到达 → lastActivityAt 刷新 + 提示清空，重新计时
    game.applyTurn(JSON.stringify({ state: 'Running', needValue: false, generation: 0 }));
    expect(game.gameStatusHint).toBeNull();
    vi.advanceTimersByTime(2000);
    expect(postSpy).toHaveBeenCalledTimes(1);
  });
});
