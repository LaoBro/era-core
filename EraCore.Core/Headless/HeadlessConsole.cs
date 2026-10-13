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
		/// <summary>宿主注入的视口真相源；null = 用自身（ScrollBar 派生）。</summary>
		private IViewportProvider? _viewport;
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

		/// <summary>
		/// M4：鼠标位置 = 输入宿主的坐标快照（console 局部，**左上基准**）。
		/// 无 <see cref="InputCapability.Geometry"/> 能力 ⇒ 快照为 null ⇒ <see cref="EmuPoint.Empty"/>（骨架前行为）。
		/// <para>
		/// ⚠️ <c>MOUSEX/MOUSEY</c> <b>不</b>读本方法 —— 它们直接读快照并换算成左下基准
		/// （<c>MY = rawY − H</c>，M0 标定），见 <c>Creator.Method.System.cs</c> 的 <c>MousePosMethod</c>。
		/// </para>
		/// </summary>
		public EmuPoint GetMousePosition() => _inputHost.Current.CursorPosition ?? EmuPoint.Empty;

		/// <summary>
		/// M4：命中判定用的光标位置。两处 pull 点（<c>ConsoleInputHandler.EndMacro</c> /
		/// <c>InputMouseKey</c>）经它取坐标再调 <c>MoveMouse</c>，故它必须反映<b>真实</b>鼠标位置。
		/// </summary>
		public EmuPoint GetCursorPosition() => _inputHost.Current.CursorPosition ?? EmuPoint.Empty;

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

		/// <summary>
		/// 视口状态。缺省是自身（由内部 ScrollBar 派生）；宿主可用
		/// <see cref="AttachViewport"/> 换成自己的真相源（CLI = <c>ScrollController</c>）。
		/// </summary>
		public IViewportProvider Viewport => _viewport ?? this;

		/// <summary>
		/// M3：把视口真相源换成宿主侧实现（CLI 的 <c>ScrollController</c>）。
		/// <para>
		/// 为什么是<b>后置注入</b>而非构造参数：CLI 的 <c>ScrollController</c> 活在
		/// <c>AgentCliProtocol</c> 里，而协议由 <c>GameLoopComposer</c> 的 <c>buildProtocol</c>
		/// 回调构造 —— 晚于本对象的创建（鸡生蛋），只能在协议构造后回填。
		/// </para>
		/// </summary>
		internal void AttachViewport(IViewportProvider viewport)
			=> _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));

		// === IViewportProvider（ADR-0031 决策 4）===
		// M3 起语义为「距底偏移」—— 原版 ScrollBar.Value 是「底行号 + 1」，它依赖总行数，
		// 而宿主侧不持有 displayLineList（ScrollController 的 lineCount 全是参数）。
		// 换算：Maximum 由 ConsoleRefreshHandler.VerticalScrollBarUpdate 维护为
		// displayLineList.Count，故 Maximum - Value 即距底行数；跟随底部时 Value == Maximum ⇒ 0。
		int IViewportProvider.ScrollOffset => _scrollBar.Maximum - _scrollBar.Value;

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
