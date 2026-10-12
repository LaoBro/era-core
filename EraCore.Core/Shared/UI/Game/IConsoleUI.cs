using System;
using MinorShift.Emuera.Primitives;

namespace MinorShift.Emuera.UI.Game
{
	/// <summary>
	/// 跨平台 UI 抽象接口。封装所有 WinForms 依赖，使 EmueraConsole 能在无头模式下运行。
	/// </summary>
	internal interface IConsoleUI
	{
		bool Created { get; }
		int ClientWidth { get; }
		int ClientHeight { get; }
		string Text { get; set; }
		bool IsActive { get; }

		void Refresh();
		void Invoke(Action action);
		void Focus();

		/// <summary>
		/// 请求关闭当前游戏会话（脚本 <c>@QUIT/@EXIT</c> 系统命令）。
		/// 注意：Headless 实现（<see cref="MinorShift.Emuera.UI.Game.HeadlessConsole"/>）可能抛出
		/// <see cref="MinorShift.Emuera.GameExitException"/> 以中止深度递归的游戏循环并触发 finally 清理；
		/// 若在交互式 UI 实现中则不抛、直接关闭窗口。宿主在调用后必须同时处理该异常与最终态退出。
		/// </summary>
		void Close();
		void Reboot();
		void ShowConfigDialog();
		void UpdateLastInput();
		void ResetTextBoxPos();
		void ClearRichText();
		void ApplyTextBoxChanges();
		void SetTextBoxPos(int xOffset, int yOffset, int width);
		void ChangeTextBox(string str);

		bool TextBoxPosChanged { get; }
		bool TextBoxIgnoreScrollBarChanges { get; set; }

		EmuPoint GetMousePosition();
		EmuPoint GetCursorPosition();

		/// <summary>
		/// 请求立即终止整个程序（脚本 <c>FORCE_QUIT</c> 系统命令，非重启路径）。
		/// 注意：Headless 实现（<see cref="MinorShift.Emuera.UI.Game.HeadlessConsole"/>）可能抛出
		/// <see cref="MinorShift.Emuera.GameExitException"/> 以中止游戏循环并触发 finally 清理；
		/// 若在交互式 UI 实现中则不抛、直接退出进程。宿主在调用后必须同时处理该异常与最终态退出。
		/// </summary>
		void ExitApplication();
		void ProcessEvents();

		IScrollBar ScrollBar { get; }
		ITextBox TextBox { get; }
		IPictureBox MainPicBox { get; }

		/// <summary>
		/// 输入宿主（ADR-0031 决策 5 · Phase 1）。
		/// <c>GetMousePosition</c> / <c>GetCursorPosition</c> / <c>IsActive</c> / <c>ProcessEvents</c>
		/// 将逐步迁移至此；全部调用方切换完成后从本接口删除（Phase 2）。
		/// </summary>
		IInputHost Input { get; }

		/// <summary>视口状态（ADR-0031 决策 4）。M3 起 <c>MoveMouse</c> 改走它，不再读 <c>ScrollBar</c>/<c>MainPicBox</c>。</summary>
		IViewportProvider Viewport { get; }
	}

	internal interface IScrollBar
	{
		int Value { get; set; }
		int Maximum { get; set; }
		bool Enabled { get; set; }
	}

	internal interface ITextBox
	{
		string Text { get; set; }
		EmuColor BackColor { get; set; }
	}

	internal interface IPictureBox
	{
		int Width { get; }
		int Height { get; }
		EmuPoint PointToClient(EmuPoint point);
		EmuRectangle ClientRectangle { get; }
	}
}
