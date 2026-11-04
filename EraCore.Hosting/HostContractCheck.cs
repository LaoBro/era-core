using System;

namespace EraCore.Hosting;

/// <summary>宿主自报版本与服务端契约的兼容性判定。</summary>
public enum HostContractStatus
{
    /// <summary>宿主与契约版本完全一致。</summary>
    Ok,

    /// <summary>宿主版本落后：必须拒绝启动并报告缺失的契约版本。</summary>
    HostBehind,

    /// <summary>宿主版本超前：允许运行，但未知 awaiting 键回退快照、未知能力位按不支持处理。</summary>
    HostAhead,

    /// <summary>版本值非法（非正整数等），无法比较。</summary>
    Unknown,
}

/// <summary>版本协商结果。<see cref="MustRefuseStart"/> 只在 HostBehind 时为 true。</summary>
public sealed record HostContractCheckResult(HostContractStatus Status, string Message)
{
    public bool MustRefuseStart => Status == HostContractStatus.HostBehind;

    public bool IsCompatible => Status is HostContractStatus.Ok or HostContractStatus.HostAhead;
}

/// <summary>
/// Hosting 版本协商。比较宿主自报的 data/api 版本与契约版本：
/// <list type="bullet">
/// <item>任一宿主版本严格小于对应契约版本 → <see cref="HostContractStatus.HostBehind"/>（必须拒绝启动）。</item>
/// <item>没有落后、且任一严格大于 → <see cref="HostContractStatus.HostAhead"/>（允许运行，按保守降级）。</item>
/// <item>两者相等 → <see cref="HostContractStatus.Ok"/>。</item>
/// <item>任一值为非正整数 → <see cref="HostContractStatus.Unknown"/>。</item>
/// </list>
/// 落后优先于超前：混合方向的版本无法安全降级。
/// </summary>
public static class HostContractCheck
{
    public static HostContractCheckResult Check(
        int hostDataVersion,
        int hostApiVersion,
        int dataVersion = HostContract.DataVersion,
        int apiVersion = HostContract.ApiVersion)
    {
        if (hostDataVersion <= 0 || hostApiVersion <= 0 || dataVersion <= 0 || apiVersion <= 0)
        {
            return new HostContractCheckResult(
                HostContractStatus.Unknown,
                $"非法版本值：host data={hostDataVersion}, host api={hostApiVersion}, " +
                $"contract data={dataVersion}, contract api={apiVersion}。");
        }

        if (hostDataVersion < dataVersion || hostApiVersion < apiVersion)
        {
            return new HostContractCheckResult(
                HostContractStatus.HostBehind,
                $"宿主版本落后（host data={hostDataVersion}, api={hostApiVersion}；" +
                $"contract data={dataVersion}, api={apiVersion}），必须拒绝启动。");
        }

        if (hostDataVersion > dataVersion || hostApiVersion > apiVersion)
        {
            return new HostContractCheckResult(
                HostContractStatus.HostAhead,
                $"宿主版本超前（host data={hostDataVersion}, api={hostApiVersion}；" +
                $"contract data={dataVersion}, api={apiVersion}）；允许运行，未知 awaiting 键回退快照，" +
                "未知能力位按不支持处理。");
        }

        return new HostContractCheckResult(
            HostContractStatus.Ok,
            $"版本一致（data={dataVersion}, api={apiVersion}）。");
    }
}
