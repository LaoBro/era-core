namespace MinorShift.Emuera.UI.Game;

/// <summary>
/// 视口状态（ADR-0031 决策 4）。**渲染侧概念，与输入分离** —— 塞进
/// <see cref="MinorShift.Emuera.Input.IInputHost"/> 会让输入层重演 <see cref="IConsoleUI"/> 的臃肿。
/// </summary>
internal interface IViewportProvider
{
	/// <summary>
	/// <b>视口底部距内容底部的行数</b>。<c>0</c> = 跟随底部（正常模式），<c>&gt;0</c> = 回看历史。
	/// <para>
	/// 🔴 <b>为什么不是原版 <c>ScrollBar.Value</c> 语义</b>：原版 <c>Value</c> 是
	/// 「可视区底部行号 + 1」（<c>verticalScrollBarUpdate()</c> 里 <c>Maximum = displayLineList.Count</c>、
	/// 跟随底部时 <c>Value == Count</c>）。它<b>依赖总行数</b>，而宿主侧根本不持有
	/// <c>displayLineList</c> —— <see cref="MinorShift.Emuera.GameView.ScrollController"/>
	/// 的 <c>ScrollBy/ScrollTo/Clamp</c> 全都把 <c>lineCount</c> 当<b>参数</b>收，正是因为它不存这个数。
	/// 若沿用 <c>Value</c> 语义，<c>ScrollController</c> 将<b>无法实现本接口</b>。
	/// </para>
	/// <para>
	/// 换成「距底偏移」后宿主只需回答「我离底部多远」，行数换算交给持有
	/// <c>displayLineList</c> 的引擎：<c>bottomLineNo = lineCount - ScrollOffset - 1</c>。
	/// 当 <c>ScrollBar.Maximum == lineCount</c> 时与原版<b>逐值等价</b>。
	/// </para>
	/// </summary>
	int ScrollOffset { get; }

	/// <summary>
	/// 可绘制区高度（像素）。实测 <c>== Config.WindowY</c>（ADR-0031 §8，M0 标定）。
	/// 仍由宿主提供：CLI 视口 = 终端高度、Web = 浏览器视口，均 ≠ <c>WindowY</c>。
	/// </summary>
	int VisibleHeight { get; }

	int LineHeight { get; }
}
