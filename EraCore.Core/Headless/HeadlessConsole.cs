using System;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;

namespace MinorShift.Emuera.UI.Game
{
	/// <summary>
	/// 无头模式下的 IConsoleUI 空实现。所有操作均为空或返回合理默认值。
	/// </summary>
	internal sealed class HeadlessConsole : IConsoleUI, IViewportProvider
	{
		private readonly HeadlessScrollBar _scrollBar = new();
		private readonly HeadlessTextBox _textBox;
		private readonly HeadlessPictureBox _pictureBox;
		private readonly IInputHost _inputHost;
		private readonly int _clientWidth;
		private readonly int _clientHeight;
		private readonly int _lineHeight;

		/// <param name="inputHost">
		/// 输入宿主（ADR-0031）。缺省 <see cref="NullInputHost"/> —— 无任何输入能力，
		/// 骨架阶段可观测行为与接入前一致。
		/// </param>
		public HeadlessConsole(IInputHost? inputHost = null)
		{
			_inputHost = inputHost ?? NullInputHost.Instance;
			// 构造总在 GlobalStatic.OpenScope 内进行（GameLoopComposer / Session 已保证）。
			// 构造时一次性缓存 Config 值，后续 ClientWidth/ClientHeight/BackColor 访问不再依赖
			// AsyncLocal scope——scope 外（HTTP 线程 / 后台 task / scope 释放后）读取不会 NRE。
			_clientWidth = Config.WindowX;
			_clientHeight = Config.WindowY;
			_lineHeight = Config.LineHeight;
			_textBox = new HeadlessTextBox(Config.BackColor);
			_pictureBox = new HeadlessPictureBox(_clientWidth, _clientHeight);
		}

		public bool Created => true;
		public int ClientWidth => _clientWidth;
		public int ClientHeight => _clientHeight;
		public string Text { get; set; } = string.Empty;
		public bool IsActive => true;

		public void Refresh() { }
		public void Invoke(Action action) => action?.Invoke();
		public void Focus() { }

		/// <summary>
		/// 脚本 <c>@QUIT/@EXIT</c> 系统命令的退出点。
		/// 无头模式没有可关闭的窗口，抛出 <see cref="GameExitException"/> 中止游戏循环，
		/// 由宿主（CLI <see cref="MinorShift.Emuera.HeadlessRunner"/> / Server <see cref="MinorShift.Emuera.Server.Session"/>）
		/// 在循环边界捕获后走正常清理。注意：调用方会收到该异常，需在宿主层 catch。
		/// </summary>
		public void Close() => throw new GameExitException();
		public void Reboot() { }
		public void ShowConfigDialog() { }
		public void UpdateLastInput() { }
		public void ResetTextBoxPos() { }
		public void ClearRichText() { }
		public void ApplyTextBoxChanges() { }
		public void SetTextBoxPos(int xOffset, int yOffset, int width) { }
		public void ChangeTextBox(string str) { }

		public bool TextBoxPosChanged => false;
		public bool TextBoxIgnoreScrollBarChanges { get; set; } = false;

		public EmuPoint GetMousePosition() => EmuPoint.Empty;
		public EmuPoint GetCursorPosition() => EmuPoint.Empty;

		/// <summary>
		/// 脚本 <c>FORCE_QUIT</c> 系统命令（非重启路径退出的终止点）。
		/// 无头模式没有可关闭的窗口，抛出 <see cref="GameExitException"/> 中止游戏循环，
		/// 由宿主（CLI <see cref="MinorShift.Emuera.HeadlessRunner"/> / Server <see cref="MinorShift.Emuera.Server.Session"/>）
		/// 在循环边界捕获后走正常清理。注意：调用方会收到该异常，需在宿主层 catch。
		/// </summary>
		public void ExitApplication() => throw new GameExitException();
		public void ProcessEvents() { }

		public IScrollBar ScrollBar => _scrollBar;
		public ITextBox TextBox => _textBox;
		public IPictureBox MainPicBox => _pictureBox;

		/// <summary>输入宿主（ADR-0031 决策 5 · Phase 1）。</summary>
		public IInputHost Input => _inputHost;

		public IViewportProvider Viewport => this;

		// === IViewportProvider（ADR-0031 决策 4）===
		// ScrollOffset 与既有 HeadlessScrollBar.Value 同源 —— M3 之前 MoveMouse 仍读 ScrollBar，
		// 取值必须一致，否则会引入行为差异。
		int IViewportProvider.ScrollOffset => _scrollBar.Value;

		/// <summary>实测 == Config.WindowY（M0 标定：H=480=WindowY，ADR-0031 §8）。</summary>
		int IViewportProvider.VisibleHeight => _clientHeight;

		int IViewportProvider.LineHeight => _lineHeight;
	}

	internal sealed class HeadlessScrollBar : IScrollBar
	{
		public int Value { get; set; } = 0;
		public int Maximum { get; set; } = 0;
		public bool Enabled { get; set; } = false;
	}

	internal sealed class HeadlessTextBox : ITextBox
	{
		public string Text { get; set; } = string.Empty;
		public EmuColor BackColor { get; set; }

		/// <summary>构造时由 HeadlessConsole 注入 Config.BackColor 快照（scope 内取值）。</summary>
		internal HeadlessTextBox(EmuColor backColor) => BackColor = backColor;
	}

	internal sealed class HeadlessPictureBox : IPictureBox
	{
		public int Width { get; }
		public int Height { get; }

		/// <summary>构造时由 HeadlessConsole 注入 Config.WindowX/Y 快照（scope 内取值）。</summary>
		internal HeadlessPictureBox(int width, int height)
		{
			Width = width;
			Height = height;
		}

		public EmuPoint PointToClient(EmuPoint point) => point;
		public EmuRectangle ClientRectangle => new(0, 0, Width, Height);
	}
}
