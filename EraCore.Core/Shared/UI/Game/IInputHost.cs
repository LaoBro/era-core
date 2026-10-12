using System;
using MinorShift.Emuera.Primitives;

namespace MinorShift.Emuera.UI.Game
{
	/// <summary>
	/// 输入能力位（ADR-0031 决策 3）。**静态**：宿主构造期声明，运行期不变。
	/// <para>
	/// 只定义已确认影响脚本可观测行为的 4 位。其余（<c>Wheel</c> / <c>PrimitiveMouse</c> /
	/// <c>LeaveMouse</c> / <c>CbgButtonMap</c> / <c>Tooltip</c>）是 TODO 位，待原生自绘宿主
	/// 落地时补齐 —— 一次定义 9 位会让降级矩阵变成 2^9 组合而无法写全。
	/// </para>
	/// </summary>
	[Flags]
	internal enum InputCapability
	{
		None = 0,

		/// <summary>能提供 console 局部坐标 ⇒ 支撑 <c>MOUSEX/MOUSEY</c> 与 <c>MoveMouse</c> 几何命中。</summary>
		Geometry = 1 << 0,

		/// <summary>未按下时可上报悬停 ⇒ <c>MOUSEB</c> 是真悬停而非降级。</summary>
		Hover = 1 << 1,

		/// <summary>真实键盘状态 ⇒ <c>GETKEY</c> / <c>GETKEYTRIGGERED</c>。</summary>
		KeyboardState = 1 << 2,

		/// <summary>真实窗口焦点 ⇒ <c>ISACTIVE</c>。</summary>
		Focus = 1 << 3,
	}

	internal readonly record struct InputCapabilities(InputCapability Flags)
	{
		public static InputCapabilities None => new(InputCapability.None);

		public bool Has(InputCapability capability) => (Flags & capability) != 0;
	}

	/// <summary>
	/// 输入状态快照（pull 面）。**不可变** —— 宿主用 <see cref="System.Threading.Volatile"/>
	/// 整体替换引用，引擎按需读取；保证同一次 <c>WaitInput</c> 内的高频轮询读到一致的一组值
	/// （ADR-0031 决策 2 / spec-v3 §8 L1''）。
	/// </summary>
	internal sealed record InputSnapshot
	{
		public static readonly InputSnapshot Empty = new();

		/// <summary>
		/// console 局部坐标（**左上基准**，ADR-0031 §8）；<c>null</c> = 宿主无 Geometry 能力。
		/// 允许越界：原版在输入框内 <c>MY ∈ [0, +20]</c>、在窗口外可取任意值，
		/// 引擎只做命中判定、不做「内外」判定。
		/// </summary>
		public EmuPoint? CursorPosition { get; init; }

		/// <summary><c>MOUSEB()</c> 应返回的值；<c>null</c> = 无悬停信息。</summary>
		public string? HoverValue { get; init; }

		/// <summary>窗口是否处于焦点；仅 <see cref="InputCapability.Focus"/> 为真时有意义。</summary>
		public bool Focused { get; init; } = true;
	}

	/// <summary>
	/// 统一输入宿主（ADR-0031 决策 1）。**宿主实现、引擎只读**，与
	/// <c>IVtHost</c>（ADR-0010）同构的依赖倒置。
	/// <para>
	/// TODO 能力位对应的成员（<c>SubmitPrimitiveKey</c> / <c>SubmitPrimitiveMouse</c> /
	/// <c>NotifyLeave</c>）随其能力位在 M8 一并加入，避免现在留下投机 API。
	/// </para>
	/// </summary>
	internal interface IInputHost
	{
		InputCapabilities Capabilities { get; }

		InputSnapshot Current { get; }

		void SubmitValue(string value);

		void SubmitHover(string? value);

		void SubmitGeometry(EmuPoint point);

		void NotifyFocusChanged(bool focused);
	}

	/// <summary>
	/// 视口状态（ADR-0031 决策 4）。**渲染侧概念，与输入分离** —— 塞进
	/// <see cref="IInputHost"/> 会让输入层重演 <see cref="IConsoleUI"/> 的臃肿。
	/// </summary>
	internal interface IViewportProvider
	{
		/// <summary>可视区行号（对齐原版 <c>ScrollBar.Value</c> 语义）。</summary>
		int ScrollOffset { get; }

		/// <summary>
		/// 可绘制区高度（像素）。实测 <c>== Config.WindowY</c>（ADR-0031 §8，M0 标定）。
		/// 仍由宿主提供：CLI 视口 = 终端高度、Web = 浏览器视口，均 ≠ <c>WindowY</c>。
		/// </summary>
		int VisibleHeight { get; }

		int LineHeight { get; }
	}

	/// <summary>
	/// M1 默认宿主：**无任何输入能力**。骨架阶段所有宿主走它，
	/// 使脚本可观测值与骨架前逐项一致（spec-v3 §8 L1'）。
	/// </summary>
	internal sealed class NullInputHost : IInputHost
	{
		public static readonly NullInputHost Instance = new();

		public InputCapabilities Capabilities => InputCapabilities.None;

		public InputSnapshot Current => InputSnapshot.Empty;

		public void SubmitValue(string value) { }

		public void SubmitHover(string? value) { }

		public void SubmitGeometry(EmuPoint point) { }

		public void NotifyFocusChanged(bool focused) { }
	}
}
