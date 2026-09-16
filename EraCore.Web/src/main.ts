import { createApp } from 'vue';
import { createPinia } from 'pinia';
import App from './App.vue';
import './styles/theme.css';
import './styles/fonts.css';
import './styles/legacy-webview.css';

/**
 * 焦点模态追踪（DESIGN.md：输入框与控件不做指针聚焦高亮，但保留键盘焦点可达性）。
 *
 * Chrome/Safari 对 text input 无论鼠标点击还是键盘聚焦都会匹配 :focus-visible
 * （用户可能立即键入），因此单纯依赖 :focus-visible 无法区分「指针点击」与
 * 「键盘导航」。这里记录最近一次输入方式：指针交互时给 <html> 加
 * .pointer-interaction，theme.css 据此抑制焦点环；键盘导航键按下即移除，
 * 恢复全局焦点环。
 */
const NAVIGATION_KEYS = new Set([
  'Tab',
  'ArrowUp',
  'ArrowDown',
  'ArrowLeft',
  'ArrowRight',
  'Home',
  'End',
  'PageUp',
  'PageDown',
  'Enter',
  ' ',
]);

function onPointerDown(): void {
  document.documentElement.classList.add('pointer-interaction');
}

function onKeyDown(e: KeyboardEvent): void {
  if (e.metaKey || e.altKey || e.ctrlKey) return;
  if (NAVIGATION_KEYS.has(e.key)) {
    document.documentElement.classList.remove('pointer-interaction');
  }
}

document.addEventListener('pointerdown', onPointerDown, true);
document.addEventListener('keydown', onKeyDown, true);

const app = createApp(App);
app.use(createPinia());
app.mount('#app');
