import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { useGameStore } from './game';
import type { ControlEvent, ControlStatus, ControllerInfo, DisplaySnapshot } from '../types/protocol';
import { isMauiEnvironment, postInput } from '../lib/mauiBridge';

export type ConnectionStatus = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

/**
 * 自动重连最大尝试次数——达到后停止，UI 显示"连接失败"。
 *
 * 选 10 次的依据：1s → 2s → 4s → 8s → 16s → 30s → 30s → 30s → 30s → 30s ≈ 2.5 分钟，
 * 足够覆盖临时网络抖动 + 用户重启 C# server 的时间，又不至于让 UI 卡死太久。
 */
export const MAX_RECONNECT_ATTEMPTS = 10;
/** 自动重连初始延迟（毫秒）——第 1 次重试等待 1s。 */
export const INITIAL_BACKOFF_MS = 1000;
/** 自动重连最大延迟（毫秒）——指数退避封顶 30s。 */
export const MAX_BACKOFF_MS = 30000;

/**
 * 指数退避计算——纯函数，便于单测。
 *
 * attempt=0 → 1s（首次重试）
 * attempt=1 → 2s
 * attempt=2 → 4s
 * ...
 * attempt≥4 → 封顶 30s（1·2^5=32s 已超 30s，attempt=4 时 16s 仍未封顶；attempt=5 时 32s→30s）
 *
 * 调用方负责在调用前递增 retryCount。
 */
export function computeBackoffMs(attempt: number): number {
  const backoff = INITIAL_BACKOFF_MS * Math.pow(2, attempt);
  return Math.min(backoff, MAX_BACKOFF_MS);
}

/**
 * useConnectionStore — WebSocket 连接生命周期（issue 06：自动重连 + snapshot 恢复）。
 *
 * 协议契约（与 C# KestrelGameServer 对齐）：
 * - **必须先 POST /session 创建会话**，server 才会接受 WS 升级；
 *   否则 server 接受 WS 升级后立即下发关闭码 4004 + reason "No active session"
 *   （KestrelGameServer.cs:271）
 * - **WS onopen 后立即 GET /snapshot**——C# server 不在首帧 diff 重放历史 PRINT 输出，
 *   故前端必须主动拉快照渲染初始画面（issue 06 明确此举；issue 03 的"等首帧再决定"方案
 *   被 issue 06 替换为更可靠的"onopen 立即拉"）。snapshot 恢复覆盖所有晚加入者场景：
 *   断线重连、多标签观察、调试工具中途连接、刷新浏览器页面
 * - 服务端发送：裸 JSON 文本帧（每个帧是一个 TurnRecord v6 JSON）
 * - 客户端发送：`{"type":"input","value":"..."}` 文本帧
 *   （C# HandleWsInput 直接入队，AgentJsonlProtocol 校验 type=="input"）
 *
 * 重连策略（issue 06）：
 * - 异常断开（onclose code !== 1000）→ status='reconnecting' → 调度 setTimeout
 * - 指数退避：1s → 2s → 4s → 8s → 16s → 30s（封顶）
 * - 重试 MAX_RECONNECT_ATTEMPTS=10 次后停止：reconnectFailed=true + status='disconnected'
 * - 重连成功（onopen）后 retryCount 清零
 * - 手动重连按钮：retryConnect() 重置 retryCount + 立即 connect
 * - 用户主动 disconnect()：取消 pending 重连定时器 + isManualDisconnect flag 抑制 onclose 重连
 */
export const useConnectionStore = defineStore('connection', () => {
  /** 当前连接状态。 */
  const status = ref<ConnectionStatus>('disconnected');
  /** 当前 WebSocket 实例（仅供本 store 内部使用，非响应式）。 */
  let ws: WebSocket | null = null;
  /**
   * 上次连接的 server URL，重连时复用。
   *
   * 默认 URL 按运行环境区分：
   * - **dev**（`import.meta.env.DEV`）：Vite dev server 5173，由 vite.config.ts
   *   proxy `/ws` 与 HTTP 端点到 C# Kestrel 8080
   * - **prod**（构建产物）：浏览器当前页面的同源 `ws://host:port/ws`——
   *   生产模式下 Vue app 由 C# Kestrel wwwroot 服务，访问 `http://localhost:8080`
   *   时同源 WS 即 `ws://localhost:8080/ws`，无需配置
   *
   * `window.location.host` 含端口，故 host:port 一起取——避免硬编码端口
   * 在用户改了 C# server 端口时需要改前端代码。
   */
  const defaultServerUrl = import.meta.env.DEV
    ? 'ws://localhost:5173/ws'
    : `${window.location.protocol === 'https:' ? 'wss://' : 'ws://'}${window.location.host}/ws`;
  const serverUrl = ref<string>(defaultServerUrl);
  /** WS 关闭时收到的 reason/错误信息（用于 UI 诊断）。 */
  const closeReason = ref<string | null>(null);
  /** 当前重连尝试次数（成功 onopen 后清零）。每次 scheduleReconnect 调用 ++。 */
  const retryCount = ref<number>(0);
  /** 重连失败上限标志——true 时 UI 显示"连接失败，请检查服务器" + 手动重连按钮。 */
  const reconnectFailed = ref<boolean>(false);
  /** 待执行的重连定时器——非响应式；disconnect() 时清除。null 表示无 pending 重连。 */
  let reconnectTimeoutId: ReturnType<typeof setTimeout> | null = null;
  /** 用户主动断开标志——onclose 看到此 flag 不调度重连。connect() 时重置为 false。 */
  let isManualDisconnect = false;
  /** 当前 Controller。null = 空闲（任何人可输入）。 */
  const controller = ref<ControllerInfo | null>(null);
  /** C# Controller.State：idle | held。 */
  const controlState = ref<string>('idle');
  /** 最近一次控制事件（多标签靠 /control/wait 收敛）。 */
  const lastControlEvent = ref<ControlEvent | null>(null);
  /** 输入/生命周期被拒时的可见错误，不静默。 */
  const controlError = ref<string | null>(null);
  /** 取消 GET /control/wait 长轮询。 */
  let controlAbort: AbortController | null = null;

  const isSpectator = computed(() => controller.value?.kind === 'agent');
  const canInput = computed(() => controller.value?.kind !== 'agent');
  const canMutateLifecycle = computed(() => controller.value?.kind !== 'agent');
  /**
   * 关联的 game store——store 顶层声明，供 connect / refreshSnapshot 共享同一引用。
   * Pinia store 是单例，useGameStore() 多次调用返回同一实例。
   */
  const game = useGameStore();

  /**
   * 派生 HTTP base URL——从 serverUrl（ws://host:port/ws）推导同源 http://host:port。
   *
   * Dev 模式：serverUrl='ws://localhost:5173/ws' → httpBase='http://localhost:5173'
   *   POST /session / GET /snapshot 经 Vite 代理到 C# 8080（见 vite.config.ts proxy）
   * 生产模式：serverUrl='ws://localhost:8080/ws' → httpBase='http://localhost:8080'
   *   请求直达 C# Kestrel。
   */
  function deriveHttpBase(url: string): string {
    // ws:// → http://, wss:// → https://；并去掉末尾的 /ws 路径
    const httpScheme = url.startsWith('wss://') ? 'https://' : 'http://';
    const rest = url.replace(/^wss?:\/\//, '');
    const hostPort = rest.split('/')[0]; // 取 host:port，丢弃 /ws 等路径
    return `${httpScheme}${hostPort}`;
  }

  /**
   * 调 `POST /session` 创建会话——WS 升级前的前置步骤。
   *
   * 返回值约定：
   * - 成功（201）：会话已创建，可继续后续步骤
   * - 已有活跃会话（409）：视为成功——上个会话还活着，可直接连入
   *   （KestrelGameServer.cs:104 的 conflict 分支；多观察者场景的关键路径）
   * - 其他失败（网络错误 / 4xx / 5xx）：抛 Error，调用方决定如何展示
   */
  async function ensureSession(httpBase: string): Promise<void> {
    const resp = await fetch(`${httpBase}/session`, { method: 'POST' });

    if (resp.status === 201 || resp.status === 409) {
      return;
    }

    // 非 201/409——读 body 做诊断。Response body 是一次性 stream，故先 text() 一次，
    // 再尝试 JSON.parse 提取 error 字段——失败则用原文作 detail。
    let detail = '';
    try {
      const text = await resp.text();
      try {
        const body = JSON.parse(text);
        detail = typeof body?.error === 'string' ? body.error : text;
      } catch {
        detail = text;
      }
    } catch {
      // body 读取失败（极罕见，如连接被中断）——detail 留空
    }
    throw new Error(`POST /session 失败：HTTP ${resp.status}${detail ? ` (${detail})` : ''}`);
  }

  /** GET /snapshot 503 重试上限与间隔——C# POST /session 后 _displayState 极短窗口为 null。
   *
   * 选 30 次 × 200ms = 6s 总窗口：覆盖大多数游戏启动场景（OpenScope + 构造 console +
   * ERB 解析 + 首帧 BuildTurn）。原 5 × 100ms = 500ms 在慢机或首次启动时不够，
   * 表现为前端首帧 turn 到达后画面仍空白（首帧 turn diff 必为 null——见 onmessage fallback 注释）。
   */
  const SNAPSHOT_503_RETRIES = 30;
  const SNAPSHOT_503_INTERVAL_MS = 200;

  /**
   * 调 `GET /snapshot` 拿当前全量显示状态——WS onopen 后立即调用。
   *
   * C# `Session.GetDisplaySnapshot()`（Session.cs:34）：
   * - session 未初始化（_displayState==null，POST /session 后极短窗口）→ 503
   * - session 运行中 / 已结束 → 200，body = DisplaySnapshot JSON
   * - 无 session → 404
   *
   * 503 重试：POST /session 把游戏循环排到独立 Task，DisplayState 在 GameLoopAsync
   * 内构造——客户端立即 GET /snapshot 时可能撞上 null 窗口。重试 SNAPSHOT_503_RETRIES
   * 次，每次间隔 SNAPSHOT_503_INTERVAL_MS——通常 1-2 次内即可成功。
   *
   * 返回：解析后的 DisplaySnapshot（不直接返回 JSON 字符串——此函数是协议边界，
   * 解析失败立即抛错，避免下游组件处理半结构化数据）。
   */
  async function fetchSnapshot(httpBase: string): Promise<DisplaySnapshot> {
    let lastError: Error | null = null;

    for (let attempt = 0; attempt < SNAPSHOT_503_RETRIES; attempt++) {
      const resp = await fetch(`${httpBase}/snapshot`);

      if (resp.status === 200) {
        const text = await resp.text();
        try {
          const obj = JSON.parse(text);
          // 用 parseTurnRecord 的字段校验逻辑做最小形状校验——DisplaySnapshot 与
          // TurnRecord 顶层结构部分重叠（state/inputType/needValue/protocolVersion），
          // 但 DisplaySnapshot 必有 lines 字段。这里独立校验 lines。
          if (!Array.isArray(obj?.lines)) {
            throw new Error('snapshot.lines 缺失或非数组');
          }
          if (typeof obj.state !== 'string') {
            throw new Error('snapshot.state 缺失或非 string');
          }
          if (typeof obj.needValue !== 'boolean') {
            throw new Error('snapshot.needValue 缺失或非 boolean');
          }
          if (typeof obj.generation !== 'number') {
            throw new Error('snapshot.generation 缺失或非 number');
          }
          return obj as DisplaySnapshot;
        } catch (e) {
          throw new Error(`GET /snapshot 解析失败：${e instanceof Error ? e.message : String(e)}`);
        }
      }

      if (resp.status === 503) {
        // session 未初始化——稍等重试
        lastError = new Error('session 尚未初始化');
        await delay(SNAPSHOT_503_INTERVAL_MS);
        continue;
      }

      // 其他状态码（404 / 5xx 等）——读 body 诊断，立即抛错不重试
      let detail = '';
      try {
        const text = await resp.text();
        try {
          const body = JSON.parse(text);
          detail = typeof body?.error === 'string' ? body.error : text;
        } catch {
          detail = text;
        }
      } catch {
        // body 读取失败——detail 留空
      }
      throw new Error(`GET /snapshot 失败：HTTP ${resp.status}${detail ? ` (${detail})` : ''}`);
    }

    throw new Error(
      `GET /snapshot 重试 ${SNAPSHOT_503_RETRIES} 次仍 503${lastError ? `：${lastError.message}` : ''}`,
    );
  }

  /** Promise-based setTimeout——503 重试间隔。 */
  function delay(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  /** 清除 pending 重连定时器——connect/retryConnect/disconnect 共用。 */
  function clearPendingReconnect(): void {
    if (reconnectTimeoutId !== null) {
      clearTimeout(reconnectTimeoutId);
      reconnectTimeoutId = null;
    }
  }

  /**
   * 调度下一次重连——若已达上限则置 reconnectFailed=true 并停止。
   *
   * 触发点：
   * - WS onclose 异常断开（code !== 1000）
   * - POST /session 失败（仅在 isReconnect=true 路径——用户主动 connect 失败不自动重试）
   *
   * 幂等：若已有 pending 重连定时器，return（防 onerror + onclose 双触发）。
   * 取消：disconnect() / connect() / retryConnect() 主动清除定时器。
   */
  function scheduleReconnect(): void {
    if (isManualDisconnect) return;
    if (reconnectTimeoutId !== null) return; // 已在等待
    if (retryCount.value >= MAX_RECONNECT_ATTEMPTS) {
      reconnectFailed.value = true;
      status.value = 'disconnected';
      closeReason.value = `重连 ${MAX_RECONNECT_ATTEMPTS} 次仍失败，请检查服务器`;
      return;
    }
    const backoff = computeBackoffMs(retryCount.value);
    retryCount.value++;
    status.value = 'reconnecting';
    reconnectTimeoutId = setTimeout(() => {
      reconnectTimeoutId = null;
      void connectInternal(serverUrl.value, true);
    }, backoff);
  }

  /**
   * 连接 internal 实现——区分 initial 与 reconnect。
   *
   * isReconnect=true 时（由 scheduleReconnect 触发）：
   * - 不重置 retryCount（保留计数用于退避）
   * - POST /session 失败 → scheduleReconnect（继续重试）
   *
   * isReconnect=false 时（由 connect / retryConnect 触发）：
   * - 重置 retryCount + reconnectFailed（用户主动连接，从 0 开始）
   * - POST /session 失败 → status='disconnected'（不自动重试，让用户看错误并手动重试）
   *
   * WS onopen 后立即 refreshSnapshot——issue 06 明确：snapshot 恢复覆盖所有晚加入者场景。
   */
  async function connectInternal(url: string, isReconnect: boolean): Promise<void> {
    if (ws && (status.value === 'connected' || status.value === 'connecting')) {
      return;
    }
    serverUrl.value = url;
    closeReason.value = null;
    if (!isReconnect) {
      retryCount.value = 0;
      reconnectFailed.value = false;
    }
    status.value = isReconnect ? 'reconnecting' : 'connecting';

    const httpBase = deriveHttpBase(url);

    // 步骤 1：创建会话——失败按 isReconnect 区分处理
    try {
      await ensureSession(httpBase);
    } catch (e) {
      closeReason.value = e instanceof Error ? e.message : String(e);
      if (isReconnect) {
        scheduleReconnect();
      } else {
        status.value = 'disconnected';
      }
      return;
    }

    // 在 ensureSession 期间用户可能 disconnect()——此时 status='disconnected'，放弃
    if (status.value !== 'connecting' && status.value !== 'reconnecting') return;

    // 步骤 2：升级 WebSocket
    const socket = new WebSocket(url);
    ws = socket;

    socket.onopen = () => {
      status.value = 'connected';
      // 成功连接后重置退避计数——下次断线从 1s 开始重新退避
      retryCount.value = 0;
      reconnectFailed.value = false;
      // Issue 06：连接建立后立即拉 snapshot 恢复全量状态（覆盖晚加入者场景）。
      // pendingSnapshotPromise 由 refreshSnapshot 设置，onmessage 会 await 它
      // 再应用 delta——保证 spec 要求的"先 snapshot 后 delta"顺序，避免
      // delta 帧先于 snapshot resolve 到达被覆盖的竞态。
      pendingSnapshotPromise = refreshSnapshot(httpBase);
      void refreshControl(httpBase);
      void startControlWatch(httpBase);
    };

    socket.onmessage = async (event) => {
      // C# SendLoopAsync 把 turn 字符串作为单个 Text 帧下发，data 即原始 JSON。
      const data = typeof event.data === 'string' ? event.data : '';
      if (!data) return;
      // 若 snapshot 还在拉，等它完成再应用 delta——保证 spec 要求的
      // "先 snapshot 后 delta"顺序，避免 delta 先到后被 snapshot 覆盖。
      if (pendingSnapshotPromise) {
        try {
          await pendingSnapshotPromise;
        } catch {
          // refreshSnapshot 内部已捕获并写 closeReason——这里吞掉即可
        }
      }
      game.applyTurn(data);
      // 首帧 fallback：C# DisplayState.ComputeDiff 第一次调用必返回 null
      // （_previous==null，DisplayState.cs:166-167）——首帧 turn 永远 diff=null。
      // 若 onopen 的 refreshSnapshot 撞上 503 窗口或拿到 lines=[]（游戏循环
      // 还没产出 PRINT），displayState.lines 仍空，applyTurn 不会改它。
      // 此时再触发一次 refreshSnapshot——in-flight 检查防重复请求，最多多发一次。
      // 后续游戏循环推进后，game.displayState.lines 会被 diff 帧刷新，fallback 不再触发。
      if (game.displayState.lines.length === 0 && !snapshotInFlight) {
        void refreshSnapshot(httpBase);
      }
    };

    socket.onerror = () => {
      // 浏览器安全策略：error 事件不暴露详情。后续 onclose 会给出状态码/原因。
      closeReason.value = closeReason.value ?? 'WebSocket error (see browser console)';
    };

    socket.onclose = (event) => {
      ws = null;
      stopControlWatch();
      closeReason.value = event.reason || `code=${event.code}`;
      // 用户主动断开（disconnect()）→ 不重连；正常关闭（code=1000）→ 不重连
      if (isManualDisconnect || event.code === 1000) {
        status.value = 'disconnected';
        return;
      }
      // 异常断开 → 调度自动重连（指数退避）
      scheduleReconnect();
    };
  }

  /**
   * 公开连接入口——用户点击"连接"按钮 / App 挂载时自动调用。
   *
   * 取消任何 pending 重连定时器，重置退避计数，从 0 开始一次全新连接。
   */
  async function connect(url: string = serverUrl.value): Promise<void> {
    clearPendingReconnect();
    isManualDisconnect = false;
    await connectInternal(url, false);
  }

  /**
   * 手动重连——重连失败上限后 UI 显示"重新连接"按钮，用户点击触发。
   *
   * 语义与 connect() 等价（重置 retryCount + reconnectFailed + 立即重连），
   * 直接委托；保留独立函数名是为了让 UI 代码意图清晰、测试用例可读。
   */
  async function retryConnect(): Promise<void> {
    await connect(serverUrl.value);
  }

  /**
   * 拉 GET /snapshot 并把结果灌入 game store——onopen 后立即调用恢复全量状态。
   *
   * 返回 Promise（onopen 是 sync 回调不能 await，但 onmessage 会 await 它
   * 保证"先 snapshot 后 delta"顺序——见 connectInternal.onmessage）。
   * 失败只写 closeReason，不改 status——WS 已连接，没有初始画面不等于断开。
   *
   * in-flight 去重：onopen 触发一次 refreshSnapshot，期间若 WS 首帧到达，
   * onmessage 会 await 同一个 pendingSnapshotPromise——避免重复请求。
   */
  let snapshotInFlight = false;
  let pendingSnapshotPromise: Promise<void> | null = null;
  async function refreshSnapshot(httpBase: string): Promise<void> {
    if (snapshotInFlight) {
      // 已在拉——返回已有 Promise，让调用方 await 同一实例
      return pendingSnapshotPromise ?? Promise.resolve();
    }
    snapshotInFlight = true;
    pendingSnapshotPromise = (async () => {
      try {
        const snapshot = await fetchSnapshot(httpBase);
        game.setSnapshot(snapshot);
      } catch (e) {
        closeReason.value = e instanceof Error ? e.message : String(e);
      } finally {
        snapshotInFlight = false;
        pendingSnapshotPromise = null;
      }
    })();
    return pendingSnapshotPromise;
  }

  function parseController(raw: unknown): ControllerInfo | null {
    if (!raw || typeof raw !== 'object') return null;
    const rec = raw as { kind?: unknown; leaseExpiresAt?: unknown };
    if (rec.kind !== 'agent' && rec.kind !== 'user') return null;
    return {
      kind: rec.kind,
      leaseExpiresAt: typeof rec.leaseExpiresAt === 'string' ? rec.leaseExpiresAt : null,
    };
  }

  function applyControlStatus(status: ControlStatus): void {
    controller.value = status.controller;
    controlState.value = status.state || (status.controller ? 'held' : 'idle');
  }

  function applyControlEvent(ev: ControlEvent): void {
    lastControlEvent.value = ev;
    applyControlStatus({
      controller: ev.controller,
      state: ev.state,
    });
    if (ev.controller?.kind !== 'agent') {
      controlError.value = null;
    }
  }

  function applyControlDenied(body: {
    controller?: unknown;
    state?: unknown;
    reason?: unknown;
    code?: unknown;
    message?: unknown;
  }): void {
    applyControlStatus({
      controller: parseController(body.controller),
      state: typeof body.state === 'string' ? body.state : 'held',
    });
    const reason = typeof body.reason === 'string' ? body.reason : typeof body.code === 'string' ? body.code : '';
    if (reason === 'CONTROL_HELD_BY_AGENT' || reason === 'CONTROL_NOT_CONTROLLER' || controller.value?.kind === 'agent') {
      controlError.value = '当前由 Agent 操控，接管后可操作';
    } else if (typeof body.message === 'string' && body.message) {
      controlError.value = body.message;
    } else {
      controlError.value = '操作被拒绝，请确认当前控制权';
    }
  }

  function stopControlWatch(): void {
    if (controlAbort) {
      controlAbort.abort();
      controlAbort = null;
    }
  }

  async function refreshControl(httpBase: string): Promise<void> {
    try {
      const resp = await fetch(`${httpBase}/control`);
      if (resp.status !== 200) {
        applyControlStatus({ controller: null, state: 'idle' });
        return;
      }
      const body = await resp.json() as { controller?: unknown; state?: unknown };
      applyControlStatus({
        controller: parseController(body.controller),
        state: typeof body.state === 'string' ? body.state : 'idle',
      });
    } catch {
      // 保留上次已知状态；控制通道失败不等于断开游戏画面
    }
  }

  async function startControlWatch(httpBase: string): Promise<void> {
    stopControlWatch();
    // 控制通道依赖 AbortController 与 fetch 的 signal 选项（均需 Chrome 66+）。
    // 旧 WebView（目标机默认 = Chromium 61）上 new AbortController 会抛 ReferenceError；调用点
    // （socket.onopen 里的 `void startControlWatch(...)`）无人接住 → 每次连接都留一条 unhandled
    // rejection，且长轮询从未真正跑起来。此处显式跳过并留痕（不静默）：
    // 该设备上控制权交接 / 旁观者事件不可用，游戏本体（WS + snapshot）不受影响。
    if (typeof AbortController === 'undefined') {
      console.warn('[connection] AbortController 不可用（需 Chrome 66+），跳过 /control/wait 长轮询');
      return;
    }
    const ac = new AbortController();
    controlAbort = ac;
    while (!ac.signal.aborted) {
      try {
        const resp = await fetch(`${httpBase}/control/wait`, { signal: ac.signal });
        if (ac.signal.aborted) return;
        if (resp.status === 200) {
          const body = await resp.json() as {
            type?: unknown;
            event?: unknown;
            reason?: unknown;
            at?: unknown;
            controller?: unknown;
            state?: unknown;
          };
          const typeRaw = typeof body.type === 'string' ? body.type : body.event;
          if (typeof typeRaw !== 'string') {
            await delay(500);
            continue;
          }
          applyControlEvent({
            type: typeRaw as ControlEvent['type'],
            reason: typeof body.reason === 'string' ? body.reason : null,
            at: typeof body.at === 'string' ? body.at : undefined,
            controller: parseController(body.controller),
            state: typeof body.state === 'string' ? body.state : 'idle',
          });
          continue;
        }
        if (resp.status === 204) continue;
        if (resp.status === 404) {
          applyControlStatus({ controller: null, state: 'idle' });
          return;
        }
        await delay(500);
      } catch {
        if (ac.signal.aborted) return;
        await delay(1000);
      }
    }
  }

  async function acquireControl(): Promise<void> {
    // Issue 05（MAUI 托管）：WebView 无 HTTP 可达（同源桥接），控制权经桥接投递——
    // C# BridgeHost.HandleControlMessage 以用户身份 acquire（agent 持有时即 steal），
    // 状态由 C# 经 controlStatus 消息推回（useAppInit handleMauiMessage 应用）。
    if (isMauiEnvironment()) {
      postInput(JSON.stringify({ type: 'control', action: 'acquire' }));
      return;
    }
    const httpBase = deriveHttpBase(serverUrl.value);
    try {
      const resp = await fetch(`${httpBase}/control/acquire`, { method: 'POST' });
      const text = await resp.text();
      let body: {
        controller?: unknown;
        state?: unknown;
        error?: unknown;
        code?: unknown;
        reason?: unknown;
        message?: unknown;
      } = {};
      try {
        body = text ? JSON.parse(text) as typeof body : {};
      } catch {
        body = {};
      }
      if (resp.status === 200) {
        applyControlStatus({
          controller: parseController(body.controller) ?? { kind: 'user', leaseExpiresAt: null },
          state: typeof body.state === 'string' ? body.state : 'held',
        });
        controlError.value = null;
        return;
      }
      applyControlDenied(body);
    } catch (e) {
      controlError.value = e instanceof Error ? e.message : String(e);
    }
  }

  /** 主动断开——取消 pending 重连 + 关闭 WS + 抑制 onclose 重连。 */
  function disconnect(): void {
    isManualDisconnect = true;
    stopControlWatch();
    clearPendingReconnect();
    if (ws) {
      ws.close(1000, 'client disconnect');
      ws = null;
    }
    status.value = 'disconnected';
    retryCount.value = 0;
    reconnectFailed.value = false;
  }

  /**
   * 提交输入。HTTP 模式走 `POST /input`（无 token = 用户），以便 409 回落到控制状态；
   * MAUI 仍走 bridge `postInput`。
   *
   * ADR-0016：v6 协议用 turn.timedOut 旗标检测超时，已删除 issue 04 启发式
   * 超时检测——通知由下一帧 turn.timedOut 派生清空，submit 不需要任何额外动作。
   *
   * Issue 07 / spec ID7：MAUI 环境分支——不走 WS，用 `postInput` 投递给 C# `IJsBridge.InputReceived`，
   * `BridgeHost.OnInputFromJs` 识别 `{"type":"input","value":"..."}` 后（T08）入 `MauiBridgeIO.EnqueueInput`。
   * MAUI 模式下 status 由 useAppInit 标记为 'connected'（无 WS 但语义等价）。
   */
  async function sendInput(value: string): Promise<void> {
    const payload = JSON.stringify({ type: 'input', value });
    // 提交输入即标记"游戏开始干活"——静默监控据此在长时间无帧时探测线程存活
    // （慢回合计算期间前端 state 停留在旧 WaitInput，只有提交时刻能区分"在忙"与"在等人"）。
    game.markInputSubmitted();
    if (isMauiEnvironment()) {
      postInput(payload);
      return;
    }
    if (status.value !== 'connected') return;
    const httpBase = deriveHttpBase(serverUrl.value);
    try {
      const resp = await fetch(`${httpBase}/input`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value }),
      });
      if (resp.status === 409) {
        let body: { controller?: unknown; state?: unknown; reason?: unknown; code?: unknown; message?: unknown } = {};
        try {
          body = await resp.json() as typeof body;
        } catch {
          body = {};
        }
        applyControlDenied(body);
        game.clearInputInFlight();
        return;
      }
      if (!resp.ok) {
        controlError.value = `输入失败：HTTP ${resp.status}`;
        game.clearInputInFlight();
      }
    } catch (e) {
      controlError.value = e instanceof Error ? e.message : String(e);
      game.clearInputInFlight();
    }
  }

  return {
    status,
    serverUrl,
    closeReason,
    retryCount,
    reconnectFailed,
    controller,
    controlState,
    lastControlEvent,
    controlError,
    isSpectator,
    canInput,
    canMutateLifecycle,
    connect,
    disconnect,
    retryConnect,
    sendInput,
    acquireControl,
    applyControlStatus,
    applyControlEvent,
    applyControlDenied,
    // 测试 seam：导出内部纯函数便于单测
    deriveHttpBase,
    ensureSession,
    fetchSnapshot,
  };
});
