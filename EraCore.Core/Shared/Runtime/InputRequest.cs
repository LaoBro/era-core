namespace MinorShift.Emuera.Runtime;

enum InputType
{
	EnterKey = 1,//Enterキーかクリック
	AnyKey = 2,//なんでもいいから入力
	IntValue = 3,//整数値。OneInputかどうかは別の変数で
	StrValue = 4,//文字列。
	Void = 5,//入力不能。待つしかない→スキップ中orマクロ中ならなかったことになる
	#region EE_INPUTANY
	AnyValue = 6,//数値or文字列
	#endregion
	#region EE_BINPUT
	IntButton = 7,
	StrButton = 8,
	#endregion

	//1823
	PrimitiveMouseKey = 11,

}


// 1819追加 入力・表示系とData、Process系の結合を弱くしよう計画の一つ
// できるだけ間にクッションをおいていきたい。最終的には別スレッドに

//クラスを毎回使い捨てるのはどうなんだろう 使いまわすべきか
internal sealed class InputRequest
{
	public InputRequest()
	{
		ID = LastRequestID++;
	}
	public readonly long ID;
	public InputType InputType;
	public bool NeedValue
	{
		get
		{
			#region EE_INPUTANY
			return InputType == InputType.IntValue || InputType == InputType.StrValue
				|| InputType == InputType.PrimitiveMouseKey || InputType == InputType.AnyValue
				|| InputType == InputType.IntButton || InputType == InputType.StrButton;
			#endregion
		}
	}
	#region EM_私家版_INPUT系機能拡張
	public bool MouseInput;
	#endregion
	public bool OneInput;
	public bool StopMesskip;
	public bool IsSystemInput;

	public bool HasDefValue;
	public long DefIntValue;
	public string DefStrValue = null!;

	public long Timelimit = -1;
	public bool DisplayTime;
	public string TimeUpMes = null!;

	/// <summary>
	/// issue-003：定时等待（<see cref="InputType.Void"/>）可否被客户端输入打断。
	///
	/// 参考实现里 Void 的语义是「入力不能、待つしかない」——<c>PressEnterKey</c> 对 Void
	/// 直接 return，输入被吞掉。本移植保留该语义作为<b>默认</b>（<c>TWAIT t,1</c>），
	/// 仅 <c>AWAIT</c> 置位：issue-003 的验收要求长动画期间按任意键可中断。
	///
	/// 未置位时输入仍会被安全吞掉（不会走到 <c>DispatchInput</c> 的 default 分支抛异常）。
	/// </summary>
	public bool Skippable;

	/// <summary>
	/// 让路点：非真实等待。<c>InputType=Void</c> + <see cref="Timelimit"/><c>=0</c> +
	/// <see cref="IsYield"/><c>=true</c> 三者共同描述「零时长挂起」——脚本在此停下，
	/// 宿主应立即交付一帧、并在同线程调用续跑入口（引擎侧 <c>EmueraConsole.EndYield</c>）；
	/// 续跑期间<b>不得</b>置 <c>isTimeout</c>（否则脚本变量 <c>ISTIMEOUT</c> 长期说谎）。
	///
	/// <para>只由无参 <c>AWAIT</c> / <c>AWAIT 0</c> 置位（<c>ConsoleStateManager.Await</c>），
	/// 与 <c>TWAIT t,1</c> 的 Void 等待（<see cref="Timelimit"/>&gt;0，走超时续跑）区分开。
	/// <b>不上 wire</b>：它只作服务端让路识别（drain）的判据。</para>
	/// </summary>
	public bool IsYield;

	static long LastRequestID;
}
