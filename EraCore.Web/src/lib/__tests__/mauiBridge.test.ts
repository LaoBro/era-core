import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
  isMauiEnvironment,
  postInput,
  registerTurnHandler,
  registerMessageHandler,
  sendReady,
  pickGameFolder,
  loadGameFromPath,
  setAgentLogEnabled,
  setDisplayReportOverride,
  getAgentLog,
  exportAgentLog,
} from '../mauiBridge';

/**
 * mauiBridge 单测——issue 07 / spec ID7 + issue 09 文件选择器。
 *
 * 覆盖：
 * - isMauiEnvironment：协议检测（ms-appx-web: / file: / http: / https: / SSR）
 * - postInput：feature detection（chrome.webview / appBridge / 两者都不存在）
 * - registerTurnHandler：window.__onTurn 注册 + 调用契约
 * - registerMessageHandler：window.__onMessage 注册 + 调用契约（issue 09）
 * - sendReady：发送 {"type":"ready"} 消息
 * - pickGameFolder：发送 {"type":"pickFolder"} 消息（issue 09）
 * - loadGameFromPath：发送 {"type":"loadGame","path":...} 消息（issue 09）
 *
 * 测试环境：Vitest node 环境（无 window），用 vi.stubGlobal 模拟 window。
 * mauiBridge.ts 内部用 `typeof window === 'undefined'` 防 SSR——stub 前调 isMauiEnvironment 应返 false。
 */

/** 模拟 window 对象——location.protocol/hostname 可按用例覆盖，chrome/appBridge 按需挂。 */
function makeMockWindow(protocol: string = 'http:', hostname: string = 'localhost'): any {
  return {
    location: { protocol, hostname },
    chrome: undefined,
    appBridge: undefined,
    __onTurn: undefined,
    __onMessage: undefined,
  };
}

describe('mauiBridge (issue 07 / spec ID7)', () => {
  let mockWindow: any;

  beforeEach(() => {
    mockWindow = makeMockWindow();
    vi.stubGlobal('window', mockWindow);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  /** 切换 location.protocol/hostname——重新 stub 整个 window 让 mauiBridge.ts 读到新值。 */
  function setLocation(protocol: string, hostname: string = 'localhost'): void {
    mockWindow = makeMockWindow(protocol, hostname);
    vi.stubGlobal('window', mockWindow);
  }

  // ===== isMauiEnvironment =====

  describe('isMauiEnvironment', () => {
    it('ms-appx-web: 协议 → true（Windows MAUI packaged）', () => {
      setLocation('ms-appx-web:');
      expect(isMauiEnvironment()).toBe(true);
    });

    it('file: 协议 → true（Android MAUI）', () => {
      setLocation('file:');
      expect(isMauiEnvironment()).toBe(true);
    });

    it('https: + hostname=app.local → true（Windows MAUI unpackaged 虚拟主机映射）', () => {
      setLocation('https:', 'app.local');
      expect(isMauiEnvironment()).toBe(true);
    });

    it('https: + hostname=game.local → true（Android MAUI，WebViewAssetLoader 虚拟域）', () => {
      // 2026-08-08：Android 整页迁到 https://game.local/wwwroot/（file:// 页面 + https 子资源
      // 不进入 shouldInterceptRequest）。isMauiEnvironment 必须识别 game.local 主机。
      setLocation('https:', 'game.local');
      expect(isMauiEnvironment()).toBe(true);
    });

    it('https: + hostname=localhost → false（HTTPS 浏览器模式）', () => {
      setLocation('https:', 'localhost');
      expect(isMauiEnvironment()).toBe(false);
    });

    it('http: 协议 → false（HTTP 模式）', () => {
      setLocation('http:');
      expect(isMauiEnvironment()).toBe(false);
    });

    it('https: 协议但非 app.local/game.local 主机 → false（HTTPS 普通站点）', () => {
      setLocation('https:', 'example.com');
      expect(isMauiEnvironment()).toBe(false);
    });

    it('window 未定义（SSR / node 环境）→ false', () => {
      vi.unstubAllGlobals(); // 移除 window stub
      expect(isMauiEnvironment()).toBe(false);
    });
  });

  // ===== postInput =====

  describe('postInput', () => {
    it('window.chrome.webview 存在 → 调 chrome.webview.postMessage（Windows）', () => {
      const postMessage = vi.fn();
      mockWindow.chrome = { webview: { postMessage } };

      postInput('{"type":"ready"}');

      expect(postMessage).toHaveBeenCalledOnce();
      expect(postMessage).toHaveBeenCalledWith('{"type":"ready"}');
    });

    it('window.appBridge 存在 → 调 appBridge.postMessage（Android）', () => {
      const postMessage = vi.fn();
      mockWindow.appBridge = { postMessage };

      postInput('{"type":"input","value":"1"}');

      expect(postMessage).toHaveBeenCalledOnce();
      expect(postMessage).toHaveBeenCalledWith('{"type":"input","value":"1"}');
    });

    it('chrome.webview 优先于 appBridge（Windows 平台优先）', () => {
      const chromePost = vi.fn();
      const bridgePost = vi.fn();
      mockWindow.chrome = { webview: { postMessage: chromePost } };
      mockWindow.appBridge = { postMessage: bridgePost };

      postInput('test');

      expect(chromePost).toHaveBeenCalledOnce();
      expect(bridgePost).not.toHaveBeenCalled();
    });

    it('两者都不存在 → 静默 no-op（不抛错）', () => {
      expect(() => postInput('test')).not.toThrow();
    });
  });

  // ===== registerTurnHandler =====

  describe('registerTurnHandler', () => {
    it('注册 window.__onTurn 函数', () => {
      registerTurnHandler(() => {});

      expect(typeof mockWindow.__onTurn).toBe('function');
    });

    it('C# 传 turn 对象 → handler 收到 JSON 字符串', () => {
      const handler = vi.fn();
      registerTurnHandler(handler);

      const turn = { state: 'WaitInput', buttons: [1, 2, 3] };
      mockWindow.__onTurn(turn);

      expect(handler).toHaveBeenCalledOnce();
      const received = handler.mock.calls[0][0];
      expect(typeof received).toBe('string');
      expect(JSON.parse(received)).toEqual(turn);
    });

    it('C# 传字符串 → handler 收到原字符串（边界，正常不应发生）', () => {
      const handler = vi.fn();
      registerTurnHandler(handler);

      mockWindow.__onTurn('already-a-string');

      expect(handler).toHaveBeenCalledWith('already-a-string');
    });

    it('覆盖前次注册（多次调用幂等）', () => {
      const handler1 = vi.fn();
      const handler2 = vi.fn();
      registerTurnHandler(handler1);
      registerTurnHandler(handler2);

      mockWindow.__onTurn({ ok: true });

      expect(handler1).not.toHaveBeenCalled();
      expect(handler2).toHaveBeenCalledOnce();
    });
  });

  // ===== sendReady =====

  describe('sendReady', () => {
    it('调 postInput 投递 {"type":"ready"} 消息（Windows chrome.webview）', () => {
      const postMessage = vi.fn();
      mockWindow.chrome = { webview: { postMessage } };

      sendReady();

      expect(postMessage).toHaveBeenCalledOnce();
      const sent = postMessage.mock.calls[0][0];
      expect(JSON.parse(sent)).toEqual({ type: 'ready' });
    });

    it('调 postInput 投递 {"type":"ready"} 消息（Android appBridge）', () => {
      const postMessage = vi.fn();
      mockWindow.appBridge = { postMessage };

      sendReady();

      expect(postMessage).toHaveBeenCalledOnce();
      const sent = postMessage.mock.calls[0][0];
      expect(JSON.parse(sent)).toEqual({ type: 'ready' });
    });

    it('无桥接对象时静默 no-op（不抛错）', () => {
      expect(() => sendReady()).not.toThrow();
    });
  });

  // ===== registerMessageHandler (issue 09) =====

  describe('registerMessageHandler', () => {
    it('注册 window.__onMessage 函数', () => {
      registerMessageHandler(() => {});

      expect(typeof mockWindow.__onMessage).toBe('function');
    });

    it('C# 传消息对象 → handler 收到原对象（非 turn 通道不 JSON.stringify）', () => {
      const handler = vi.fn();
      registerMessageHandler(handler);

      const msg = { type: 'folderPicked', path: 'D:\\games\\mygame' };
      mockWindow.__onMessage(msg);

      expect(handler).toHaveBeenCalledOnce();
      expect(handler).toHaveBeenCalledWith(msg);
    });

    it('覆盖前次注册（多次调用幂等）', () => {
      const handler1 = vi.fn();
      const handler2 = vi.fn();
      registerMessageHandler(handler1);
      registerMessageHandler(handler2);

      mockWindow.__onMessage({ type: 'folderPicked', path: '/x' });

      expect(handler1).not.toHaveBeenCalled();
      expect(handler2).toHaveBeenCalledOnce();
    });

    it('与 registerTurnHandler 互不干扰——__onTurn / __onMessage 独立', () => {
      const turnHandler = vi.fn();
      const msgHandler = vi.fn();
      registerTurnHandler(turnHandler);
      registerMessageHandler(msgHandler);

      mockWindow.__onTurn({ state: 'WaitInput' });
      mockWindow.__onMessage({ type: 'folderPicked', path: '/y' });

      expect(turnHandler).toHaveBeenCalledOnce();
      expect(msgHandler).toHaveBeenCalledOnce();
    });
  });

  // ===== pickGameFolder (issue 09) =====

  describe('pickGameFolder', () => {
    it('调 postInput 投递 {"type":"pickFolder"} 消息（Windows chrome.webview）', () => {
      const postMessage = vi.fn();
      mockWindow.chrome = { webview: { postMessage } };

      pickGameFolder();

      expect(postMessage).toHaveBeenCalledOnce();
      const sent = postMessage.mock.calls[0][0];
      expect(JSON.parse(sent)).toEqual({ type: 'pickFolder' });
    });

    it('调 postInput 投递 {"type":"pickFolder"} 消息（Android appBridge）', () => {
      const postMessage = vi.fn();
      mockWindow.appBridge = { postMessage };

      pickGameFolder();

      expect(postMessage).toHaveBeenCalledOnce();
      const sent = postMessage.mock.calls[0][0];
      expect(JSON.parse(sent)).toEqual({ type: 'pickFolder' });
    });

    it('无桥接对象时静默 no-op（不抛错）', () => {
      expect(() => pickGameFolder()).not.toThrow();
    });
  });

  // ===== loadGameFromPath (issue 09) =====

  describe('loadGameFromPath', () => {
    it('调 postInput 投递 {"type":"loadGame","path":...} 消息（Windows chrome.webview）', () => {
      const postMessage = vi.fn();
      mockWindow.chrome = { webview: { postMessage } };

      loadGameFromPath('D:\\games\\mygame');

      expect(postMessage).toHaveBeenCalledOnce();
      const sent = postMessage.mock.calls[0][0];
      expect(JSON.parse(sent)).toEqual({ type: 'loadGame', path: 'D:\\games\\mygame' });
    });

    it('调 postInput 投递 {"type":"loadGame","path":...} 消息（Android appBridge）', () => {
      const postMessage = vi.fn();
      mockWindow.appBridge = { postMessage };

      loadGameFromPath('/sdcard/games/mygame');

      expect(postMessage).toHaveBeenCalledOnce();
      const sent = postMessage.mock.calls[0][0];
      expect(JSON.parse(sent)).toEqual({ type: 'loadGame', path: '/sdcard/games/mygame' });
    });

    it('无桥接对象时静默 no-op（不抛错）', () => {
      expect(() => loadGameFromPath('/any/path')).not.toThrow();
    });
  });

  // ===== setAgentLogEnabled (A0) =====

  describe('setAgentLogEnabled', () => {
    it('true → 投递 {"type":"setAgentLogEnabled","enabled":true} 消息（Windows chrome.webview）', () => {
      const postMessage = vi.fn();
      mockWindow.chrome = { webview: { postMessage } };

      setAgentLogEnabled(true);

      expect(postMessage).toHaveBeenCalledOnce();
      const sent = postMessage.mock.calls[0][0];
      expect(JSON.parse(sent)).toEqual({ type: 'setAgentLogEnabled', enabled: true });
    });

    it('false → 投递 {"type":"setAgentLogEnabled","enabled":false} 消息（Android appBridge）', () => {
      const postMessage = vi.fn();
      mockWindow.appBridge = { postMessage };

      setAgentLogEnabled(false);

      expect(postMessage).toHaveBeenCalledOnce();
      const sent = postMessage.mock.calls[0][0];
      expect(JSON.parse(sent)).toEqual({ type: 'setAgentLogEnabled', enabled: false });
    });

    it('无桥接对象时静默 no-op（不抛错）', () => {
      expect(() => setAgentLogEnabled(true)).not.toThrow();
    });
  });

  // ===== setDisplayReportOverride（启动日志覆盖三态） =====

  describe('setDisplayReportOverride', () => {
    it("'on' → 投递 {\"type\":\"setDisplayReportOverride\",\"value\":\"on\"} 消息（Windows chrome.webview）", () => {
      const postMessage = vi.fn();
      mockWindow.chrome = { webview: { postMessage } };

      setDisplayReportOverride('on');

      expect(postMessage).toHaveBeenCalledOnce();
      const sent = postMessage.mock.calls[0][0];
      expect(JSON.parse(sent)).toEqual({ type: 'setDisplayReportOverride', value: 'on' });
    });

    it("'auto' → 投递 {\"type\":\"setDisplayReportOverride\",\"value\":\"auto\"} 消息（Android appBridge）", () => {
      const postMessage = vi.fn();
      mockWindow.appBridge = { postMessage };

      setDisplayReportOverride('auto');

      expect(postMessage).toHaveBeenCalledOnce();
      const sent = postMessage.mock.calls[0][0];
      expect(JSON.parse(sent)).toEqual({ type: 'setDisplayReportOverride', value: 'auto' });
    });

    it('无桥接对象时静默 no-op（不抛错）', () => {
      expect(() => setDisplayReportOverride('off')).not.toThrow();
    });
  });

  // ===== getAgentLog (A0 补充：app 内日志查看器) =====

  describe('getAgentLog', () => {
    it('投递 {"type":"getAgentLog"} 消息（Windows chrome.webview）', () => {
      const postMessage = vi.fn();
      mockWindow.chrome = { webview: { postMessage } };

      getAgentLog();

      expect(postMessage).toHaveBeenCalledOnce();
      const sent = postMessage.mock.calls[0][0];
      expect(JSON.parse(sent)).toEqual({ type: 'getAgentLog' });
    });

    it('无桥接对象时静默 no-op（不抛错）', () => {
      expect(() => getAgentLog()).not.toThrow();
    });
  });

  // ===== exportAgentLog (A0 补充：FileProvider 导出) =====

  describe('exportAgentLog', () => {
    it('投递 {"type":"exportAgentLog"} 消息（Windows chrome.webview）', () => {
      const postMessage = vi.fn();
      mockWindow.chrome = { webview: { postMessage } };

      exportAgentLog();

      expect(postMessage).toHaveBeenCalledOnce();
      const sent = postMessage.mock.calls[0][0];
      expect(JSON.parse(sent)).toEqual({ type: 'exportAgentLog' });
    });

    it('无桥接对象时静默 no-op（不抛错）', () => {
      expect(() => exportAgentLog()).not.toThrow();
    });
  });
});
