import { useGameStore } from '../stores/game';
import { getGameThreadStatus, isMauiEnvironment } from '../lib/mauiBridge';

/** 静默阈值——超过该时长无 turn 帧且不在等待输入，视为游戏"在忙"，探测线程存活。 */
const SILENT_THRESHOLD_MS = 5000;
/** 探测轮询周期。 */
const TICK_MS = 2000;

/**
 * MAUI 专属：游戏静默监控——替代已删除的死循环检测（C# 侧不再有检测逻辑）。
 *
 * 原理：游戏线程在前台运行时持续产出 turn 帧；长时间无帧 = 脚本仍在执行
 * （慢计算 / 真死循环对玩家都是"在忙"）。此时向 C# 探测一次线程存活：
 * - alive=true → 半透明「游戏运行中」提示（不打扰，新帧到达即消失）
 * - alive=false → 「游戏已停止」提示（后台线程已死而前端不知情的兜底——原 be69e76
 *   之前 adb 调试才能发现的问题场景）
 *
 * 「忙」的判定（任一满足即视为游戏在忙）：
 * - 已提交输入但长时间无帧（inputSubmittedAt 非空）——慢回合计算期间 C# 不产帧、
 *   前端 state 停留在旧的 WaitInput，只有提交时刻能标记"游戏开始干活了"
 * - 非 WaitInput 态（如 Running）长时间无帧
 *
 * 不提示的情况：
 * - 非 MAUI 环境（HTTP 模式有 WS 连接语义，且 C# 侧无此消息处理）
 * - 无活跃游戏（gameDir 为空 / serverState=Idle）
 * - 等待玩家输入且未提交输入（WaitInput + 无提交——游戏在等人不是卡死，清残留提示）
 * - 已有提示（任何新 turn 会清空提示并重新计时，此处不必重复探测）
 *
 * @returns 停止函数（clearInterval）——App 级调用可忽略，页面生命周期即应用生命周期。
 */
export function startGameStatusMonitor(): () => void {
  if (!isMauiEnvironment()) return () => {};
  const game = useGameStore();
  const interval = setInterval(() => {
    if (!game.gameDir || game.serverState === 'Idle') {
      game.clearGameStatusHint();
      return;
    }
    // 服务端停稳在「等输入」或「等定时器」（且无待处理的提交）→ 不是忙：清残留提示并退出。
    // issue 07：判据改读 awaiting —— 两个维度任一存在就说明服务端不会自己卡住
    // （等输入要玩家动作、定时等待由服务端计时器推进）；旧判据读 state === 'WaitInput'，
    // 那既涵盖了这两类、也把「WaitInput」当成引擎模式在猜。
    if (game.inputSubmittedAt === null && (game.awaiting.input != null || game.awaiting.wait != null)) {
      game.clearGameStatusHint();
      return;
    }
    if (game.gameStatusHint !== null) return;
    if (Date.now() - game.lastActivityAt > SILENT_THRESHOLD_MS) {
      getGameThreadStatus();
    }
  }, TICK_MS);
  return () => clearInterval(interval);
}
