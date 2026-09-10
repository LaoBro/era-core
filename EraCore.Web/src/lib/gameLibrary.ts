import { useGameStore } from '../stores/game';
import {
  isMauiEnvironment,
  loadGameFromPath,
  pickGameFolder,
  pickSafDirectory,
  scanGames as scanGamesBridge,
  listDirectories as listDirectoriesBridge,
  getStorageRoots as getStorageRootsBridge,
  MAUI_GAME_VIRTUAL_HOST,
  MAUI_WINDOWS_VIRTUAL_HOST,
} from './mauiBridge';

/**
 * 游戏库数据源抽象（GameLibraryView 的传输层 seam）——
 * MAUI 与 Web/HTTP 共用同一个游戏选择页（GameLibraryView），只有"如何拿数据"不同：
 *
 * - maui（mauiGameLibrarySource）：走 C# JS 桥接消息（scanGames → gamesScanned 事件回 store），
 *   选目录用原生 FolderPicker/SAF；issues/15 S2 起另支持页内**真实路径**浏览
 *   （listDirectories / storageRoots → legacy 直读模式）。
 * - http（httpGameLibrarySource）：走 C# server 的 HTTP 端点（/game/scan、/game/dirs），
 *   选目录用页面内目录浏览（浏览器沙箱无原生选择器）。
 *
 * 两者底层最终都调 Core 的 GameScanner.Scan / DirectoryLister，store 状态完全共享，
 * 所以列表页的展示/设计（GameLibraryView.vue）只需维护一份。
 */

/** 目录列举结果（与 C# `DirectoryListResult` 对称）。 */
export interface DirListResult {
  currentPath: string;
  parentPath: string | null;
  dirs: string[];
}

/**
 * issues/15 S2：可浏览的真实路径根（与 C# `StorageRootDto` 对称）。
 *
 * `path` 恒为**真实文件路径**（非 content:// URI），挂载点由 C# 侧系统 API 给出。
 */
export interface StorageRoot {
  path: string;
  label: string;
  primary: boolean;
  removable: boolean;
}

/**
 * 游戏库网关——GameLibraryView 依赖此接口驱动扫描 / 浏览 / 选目录 / 加载游戏，
 * 组件本身不感知具体传输实现。
 */
export interface GameLibrarySource {
  /** 传输标识——启动时自动扫描的行为差异用（MAUI 由 useAppInit 首扫，Web 由视图首扫）。 */
  readonly kind: 'maui' | 'http';
  /** 原生目录选择器是否可用（MAUI FolderPicker/SAF）；false 时视图改用页面内目录浏览。 */
  readonly supportsNativePicker: boolean;
  /**
   * 扫描主目录下的游戏（更新 store 的 scannedGames / scanRootDir / scanStatus）。
   * @returns true=请求成功（rootDir 不存在也算成功，rootDirExists=false）；false=传输失败（视图据此展示连接错误）。
   */
  scan(rootDir: string): Promise<boolean>;
  /** 列举目录的子目录；失败 / 不支持返回 null。 */
  listDirs(dir: string): Promise<DirListResult | null>;
  /**
   * issues/15 S2：本机可浏览的**真实路径**根清单——页内浏览器的起点。
   *
   * 仅 MAUI 需要（挂载点前端猜不出，要问 C#）；Web/HTTP 返回 null（浏览器里可直接从任意路径起浏览）。
   * 未实现 / 返回 null 时，页内浏览器退化为「从当前输入路径起浏览」。
   */
  storageRoots?(): Promise<StorageRoot[] | null>;
  /**
   * 打开原生目录选择器。MAUI：触发 FolderPicker/SAF，选中后由事件（folderPicked /
   * safDirectoryPicked → useAppInit）更新 store；此方法只触发，返回 null。
   * Web：无原生选择器，返回 null（改用页面内目录浏览）。
   */
  pickMainDir(): Promise<string | null>;
  /** 点击游戏列表项——加载该游戏（MAUI 走 bridge hot-swap，HTTP 走 /load-game）。 */
  pickGame(name: string, fullPath: string): Promise<void>;
}

/** MAUI 平台分流：Windows 用 FolderPicker，Android 用 SAF（ADR-0019）。 */
function isWindowsMaui(): boolean {
  if (!isMauiEnvironment()) return false;
  if (typeof window === 'undefined') return false;
  const { protocol, hostname } = window.location;
  return protocol === 'ms-appx-web:'
    || (protocol === 'https:' && hostname === MAUI_WINDOWS_VIRTUAL_HOST);
}

function isAndroidMaui(): boolean {
  if (!isMauiEnvironment()) return false;
  if (typeof window === 'undefined') return false;
  const { protocol, hostname } = window.location;
  return protocol === 'file:'
    || (protocol === 'https:' && hostname === MAUI_GAME_VIRTUAL_HOST);
}

/**
 * 是否 Android MAUI 环境——供上层决定平台专属入口的可见性。
 *
 * issues/15 S2 的「浏览存储目录…」只在 Android 有意义：Windows 有原生 FolderPicker，
 * 且 legacy 直读（MANAGE_EXTERNAL_STORAGE）场景只存在于 Android。
 */
export function isAndroidMauiEnvironment(): boolean {
  return isAndroidMaui();
}

/**
 * MAUI 桥接实现——游戏库数据经 C# JS 桥接事件回流（gamesScanned / folderPicked /
 * safDirectoryPicked 由 useAppInit 的 handleMauiMessage 更新 store），本实现只负责触发 + 标记扫描中。
 */
export const mauiGameLibrarySource: GameLibrarySource = {
  kind: 'maui',
  supportsNativePicker: true,

  async scan(rootDir) {
    const game = useGameStore();
    const trimmed = rootDir.trim();
    if (!trimmed) return false;
    // store 更新由 useAppInit 的 gamesScanned 消息处理器完成；这里标记扫描中 + 触发 bridge
    game.scanStatus = 'scanning';
    game.scanRootDir = trimmed;
    scanGamesBridge(trimmed);
    return true;
  },

  async listDirs(dir) {
    // issues/15 S2：MAUI 也能逐层浏览**真实路径**了——走 listDirectories 桥接消息，
    // C# 侧 HandleListDirectories 按路径形态分流 accessor（真实路径 → FileSystem）。
    // 这是 legacy 模式（MANAGE_EXTERNAL_STORAGE 授权后直读）的唯一目录入口：
    // SAF picker 只产出 content:// URI，拿不到真实路径。
    const wire = await listDirectoriesBridge(dir);
    if (wire === null) return null;
    return {
      currentPath: wire.currentPath,
      parentPath: wire.parentPath,
      dirs: wire.subDirectories,
    };
  },

  async storageRoots(): Promise<StorageRoot[] | null> {
    // 卷挂载点由系统给（Android StorageManager / Windows DriveInfo），前端不猜。
    return getStorageRootsBridge();
  },

  async pickMainDir() {
    if (isAndroidMaui()) {
      pickSafDirectory();
    } else if (isWindowsMaui()) {
      pickGameFolder();
    } else {
      console.warn('[gameLibrary] pickMainDir: not in MAUI environment');
    }
    return null;
  },

  async pickGame(name, fullPath) {
    const game = useGameStore();
    game.setLastPlayedGame(name);
    game.setGameDir(fullPath);
    game.reset();
    loadGameFromPath(fullPath);
  },
};

/**
 * HTTP 实现——游戏库数据经 C# server 的 HTTP 端点获取，直接更新 store 并返回成败。
 */
export const httpGameLibrarySource: GameLibrarySource = {
  kind: 'http',
  supportsNativePicker: false,

  async scan(rootDir) {
    const game = useGameStore();
    return game.scanGamesHttp(rootDir);
  },

  async listDirs(dir) {
    const game = useGameStore();
    return game.browseDirectoryHttp(dir);
  },

  async storageRoots(): Promise<StorageRoot[] | null> {
    // Web/HTTP 无「挂载点」概念——浏览器里可直接从任意路径起浏览，不需要根清单。
    return null;
  },

  async pickMainDir() {
    // 浏览器无原生选择器——视图用页面内目录浏览替代
    return null;
  },

  async pickGame(name, fullPath) {
    const game = useGameStore();
    game.setLastPlayedGame(name);
    await game.loadGame(fullPath);
  },
};
