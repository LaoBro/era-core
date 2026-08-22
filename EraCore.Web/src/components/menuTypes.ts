/**
 * menuTypes.ts — 游戏页「更多」菜单（DrawerMenu）与确认对话框复用的菜单项类型。
 * 从原 PopupMenu.vue 抽离，DrawerMenu 与 App.vue 共用，避免菜单实现持有类型定义。
 */

export type IconName =
  | 'sun'
  | 'moon'
  | 'restart'
  | 'terminal'
  | 'debug'
  | 'settings'
  | 'exit';

export type PopupMenuItem =
  | {
      type: 'item';
      id: string;
      label: string;
      /** 内联 SVG 图标名——继承 currentColor 渲染，规避 emoji 在 Android 上按彩色字形显示。 */
      icon?: IconName;
      danger?: boolean;
      disabled?: boolean;
      active?: boolean;
      onClick: () => void;
    }
  | {
      type: 'zoom';
      percent: number;
      canZoomOut: boolean;
      canZoomIn: boolean;
      onZoomOut: () => void;
      onZoomReset: () => void;
      onZoomIn: () => void;
    }
  | { type: 'separator' };