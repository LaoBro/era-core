namespace EraCore.Hosting;

/// <summary>
/// Hosting 契约版本。与 wire 数据版本分开：
/// - <see cref="DataVersion"/> 跟随 <c>TurnRecord.CurrentProtocolVersion</c>（DTO/wire 形状）。
/// - <see cref="ApiVersion"/> 表示门面方法/生命周期语义；破坏性变更时 bump。
/// </summary>
public static class HostContract
{
    /// <summary>DTO / wire 形状版本（当前 v13）。</summary>
    public const int DataVersion = MinorShift.Emuera.Output.TurnRecord.CurrentProtocolVersion;

    /// <summary>Hosting 门面 API 语义版本。</summary>
    public const int ApiVersion = 1;
}
