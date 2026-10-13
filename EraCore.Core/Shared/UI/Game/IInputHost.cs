using System;
using System.Threading;
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
	/// 键盘状态（<see cref="InputCapability.KeyboardState"/> 的载荷）。**不可变**，写操作返回新实例。
	/// <para>
	/// 两套语义并存，对应脚本侧两个函数：
	/// <list type="bullet">
	/// <item><b>电平</b>（<c>GETKEY</c>）：按下/松开。<see cref="IsDown"/> —— 纯读取，无副作用。</item>
	/// <item><b>边沿</b>（<c>GETKEYTRIGGERED</c>）：<see cref="IInputHost.ConsumeTrigger"/> ——
	/// <b>有副作用</b>（读到即失效），故不放进本不可变快照。</item>
	/// </list>
	/// </para>
	/// <para>
	/// ⚠️ <b>语义严格照抄原版，不做任何增强</b>（ADR-0031「core 不模拟、不补偿、不放宽」）。
	/// 原版见 <c>Creator.Method.System.cs</c> 的 <c>#if !HEADLESS</c> 分支：
	/// <c>(s &lt; 0) &amp;&amp; (toggle != keytoggle[keycode])</c>，即
	/// 「<b>按下期间</b>第一次读取返回 1，之后恒 0，直到下次按下」。
	/// 本类只提供该式所需的两个操作数：<see cref="IsDown"/>（← <c>s &lt; 0</c>）
	/// 与 <see cref="Version"/>（← <c>keytoggle[keycode]</c>）；
	/// 「上次读到的版本」由宿主持有（<c>DefaultInputHost</c>），
	/// 取代原版的 <c>static keytoggle</c> —— 作用域从进程级降为宿主级，顺带消除跨会话泄漏。
	/// </para>
	/// </summary>
	internal sealed class KeyStateSet
	{
		private const int KeyCount = 256;

		private static readonly bool[] NoDown = new bool[KeyCount];
		private static readonly int[] NoVersion = new int[KeyCount];

		public static readonly KeyStateSet Empty = new(null, null);

		private readonly bool[] _down;
		private readonly int[] _version;

		private KeyStateSet(bool[]? down, int[]? version)
		{
			_down = down ?? NoDown;
			_version = version ?? NoVersion;
		}

		private static bool InRange(int keycode) => (uint)keycode < KeyCount;

		/// <summary>电平：<c>GETKEY</c>。对应原版 <c>s &lt; 0</c>。</summary>
		public bool IsDown(int keycode) => InRange(keycode) && _down[keycode];

		/// <summary>按下版本号。对应原版 <c>keytoggle[keycode]</c> 的 toggle 位翻转。</summary>
		public int Version(int keycode) => InRange(keycode) ? _version[keycode] : 0;

		/// <summary>
		/// 返回设置后的新实例。仅 <c>false → true</c> 跃变递增版本号
		/// （对应原版 toggle 位翻转；长按自动重复的重复 down 不递增 ⇒ 不重复触发）。
		/// </summary>
		public KeyStateSet WithDown(int keycode, bool down)
		{
			if (!InRange(keycode))
				return this;
			var d = (bool[])_down.Clone();
			var v = (int[])_version.Clone();
			if (down && !d[keycode])
				v[keycode]++;
			d[keycode] = down;
			return new KeyStateSet(d, v);
		}
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

		/// <summary>键盘状态（M2）。仅 <see cref="InputCapability.KeyboardState"/> 为真时有意义。</summary>
		public KeyStateSet Keys { get; init; } = KeyStateSet.Empty;
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

		/// <summary>键盘事件（M2）。<paramref name="down"/> = 按下 / 松开。</summary>
		void SubmitKey(int keycode, bool down);

		/// <summary>
		/// 消费一次「按下边沿」（<c>GETKEYTRIGGERED</c>）。**有副作用：读到即失效**。
		/// <para>
		/// 严格照抄原版 <c>(s &lt; 0) &amp;&amp; (toggle != keytoggle[keycode])</c> ——
		/// 「<b>键正被按着的期间</b>，第一次读取返回 <c>true</c>，之后恒 <c>false</c>，直到下次按下」。
		/// <b>不做时间窗口、不做延时失效</b>（ADR-0031「core 不模拟、不补偿、不放宽」）。
		/// </para>
		/// <para>
		/// ⚠️ 因此本方法<b>不能</b>放进不可变的 <see cref="InputSnapshot"/>：读取即改变宿主状态，
		/// 与「快照是纯 pull」的语义冲突。这是接口上唯一一处有副作用的成员，代价换来语义忠实。
		/// </para>
		/// </summary>
		bool ConsumeTrigger(int keycode);

		/// <summary>
		/// 清空全部输入状态（M2）。用于会话重建 / 焦点丢失（M6）。
		/// ⚠️ **不得**在 <c>WaitInput</c> 建立时调用 —— 那属于「等待边界清除」，
		/// 是 era-core 自创语义（原版 <c>keytoggle</c> 只在读取时更新），会破坏边沿检测。
		/// </summary>
		void ResetState();
	}

	/// <summary>
	/// 视口状态（ADR-0031 决策 4）。**渲染侧概念，与输入分离** —— 塞进
	/// <see cref="IInputHost"/> 会让输入层重演 <see cref="IConsoleUI"/> 的臃肿。
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

		public void SubmitKey(int keycode, bool down) { }

		public bool ConsumeTrigger(int keycode) => false;

		public void ResetState() { }
		}

		/// <summary>
		/// 可变输入宿主（M2）。**跨线程安全**：所有写操作构造新快照后
		/// <see cref="Volatile.Write"/> 整体换引用，引擎侧只读 <see cref="Current"/>。
		/// <para>
		/// 能力位在构造期固定（ADR-0031 决策 3：静态）。缺省
		/// <see cref="InputCapability.KeyboardState"/> —— M2 只交付键盘状态，
		/// <c>Geometry</c> / <c>Focus</c> 分别留待 M4 / M6。
		/// </para>
		/// </summary>
		internal sealed class DefaultInputHost : IInputHost
		{
			private readonly InputCapabilities _capabilities;
			private InputSnapshot _current = InputSnapshot.Empty;

			/// <summary>
			/// 「上次读到的按下版本号」，取代原版的 <c>static keytoggle[]</c>。
			/// <para>
			/// <b>为什么是实例字段而非 static</b>：原版是进程级静态 ⇒ 换游戏重建会话后
			/// 残留旧版本号，可能让 <c>GETKEYTRIGGERED</c> 首次读取行为异常（跨会话泄漏）。
			/// 降为宿主级后随会话丢弃。
			/// </para>
			/// <para>
			/// <b>线程</b>：只被引擎线程（<c>GETKEYTRIGGERED</c> 调用点）读写，
			/// 宿主侧的 <see cref="SubmitKey"/> 不碰它 —— 无需同步，与原版裸数组一致。
			/// </para>
			/// </summary>
			private readonly int[] _lastRead = new int[256];

			/// <param name="capabilities">构造期声明的能力位，运行期不变。</param>
			public DefaultInputHost(InputCapability capabilities = InputCapability.KeyboardState)
				=> _capabilities = new InputCapabilities(capabilities);

			public InputCapabilities Capabilities => _capabilities;

			public InputSnapshot Current => Volatile.Read(ref _current);

			/// <summary>
			/// 值提交钩子（提交类输入）。当前不改变任何状态 ——
			/// <c>hoverValue</c> 由前端随点击显式上报（<see cref="SubmitHover"/>），
			/// 不从 <c>value</c> 推断：正常 <c>INPUT</c> 键入的数字不是「鼠标悬停」，
			/// 推断会让 <c>MOUSEB()</c> 在纯键盘流程里凭空返回值。
			/// </summary>
			public void SubmitValue(string value) { }

			public void SubmitHover(string? value) => Update(s => s with { HoverValue = value });

			public void SubmitGeometry(EmuPoint point) => Update(s => s with { CursorPosition = point });

			public void NotifyFocusChanged(bool focused) => Update(s => s with { Focused = focused });

			public void SubmitKey(int keycode, bool down) => Update(s => s with { Keys = s.Keys.WithDown(keycode, down) });

			/// <summary>
			/// 严格照抄原版 <c>(s &lt; 0) &amp;&amp; (toggle != keytoggle[keycode])</c>：
			/// <c>down</c> ← <c>s &lt; 0</c>，<c>version</c> ← <c>keytoggle[keycode]</c>，
			/// <c>_lastRead</c> ← <c>toggle</c>（读取后同步 ⇒ 下一次读取即失效）。
			/// </summary>
			public bool ConsumeTrigger(int keycode)
			{
				if ((uint)keycode >= 256)
					return false;
				KeyStateSet keys = Volatile.Read(ref _current).Keys;
				bool down = keys.IsDown(keycode);
				int version = keys.Version(keycode);
				int last = _lastRead[keycode];
				_lastRead[keycode] = version;
				return down && version != last;
			}

			public void ResetState() => Volatile.Write(ref _current, InputSnapshot.Empty);

			private void Update(Func<InputSnapshot, InputSnapshot> mutator)
			{
				while (true)
				{
					var old = Volatile.Read(ref _current);
					var next = mutator(old);
					if (ReferenceEquals(Interlocked.CompareExchange(ref _current, next, old), old))
						return;
				}
			}
		}
		}
