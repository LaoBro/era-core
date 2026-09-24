using System;
using System.Collections.Generic;

namespace MinorShift.Emuera.Runtime.Config;

/// <summary>
/// 配置键别名表——「为将来合并其它本地化分支的配置键名」留的接缝。
///
/// <para>
/// 背景：<see cref="ConfigData.GetConfigItem(string)"/> 只认三类键名：<c>Name</c>（ConfigCode 名）、
/// <c>Text</c>（日文）、<c>EngText</c>（英文）。其它本地化分支（如简体中文版 Emuera）写出的
/// emuera.config 用它自己那套中文键名，例如「忽略大小写」「在sav文件夹中创建存档」——与本引擎的
/// 三类键名都不同，于是这些键读不到、值回落默认。这正是「EraCore 打开一次后原版 Emuera 满屏报错」
/// 事故的成因之一。
/// </para>
/// <para>
/// 注意：<c>lang/emuera-zhs.xml</c> <b>不能</b>用来推导这张表——它存的是 UI 标签
/// （「使用鼠标」「将存档保存到sav文件夹」「历史日志行数」），与配置<b>文件</b>里的键名
/// （「启用鼠标」「在sav文件夹中创建存档」「记录日志的行数」）是两套不同字符串（已逐条比对）。
/// 因此数据必须显式登记，来源是上游简体中文分支的源码。
/// </para>
/// <para>
/// 填写方式（取得源码后逐项补齐即可，无需改动其它代码）：
/// <code>
/// private static readonly Dictionary&lt;ConfigCode, string[]&gt; Map = new()
/// {
///     [ConfigCode.IgnoreCase] = ["忽略大小写"],
///     [ConfigCode.UseSaveFolder] = ["在sav文件夹中创建存档"],
/// };
/// </code>
/// 校验：ConfigKeyAliasesTests 断言 ①别名非空；②不与任何 Name/Text/EngText 冲突；
/// ③一个别名不被两个 ConfigCode 同时声明。填错会在测试里立刻暴露。
/// </para>
/// </summary>
internal static class ConfigKeyAliases
{
	/// <summary>ConfigCode → 该 Code 额外接受的键名（除 Name/Text/EngText 之外）。</summary>
	private static readonly Dictionary<ConfigCode, string[]> Map = new()
	{
		// TODO(cn-merge)：待上游简体中文分支（如 Emuera1821 cn 等）源码到位后逐项填入。
		// 示例格式：[ConfigCode.IgnoreCase] = ["忽略大小写"],
	};

	/// <summary>全部别名声明（构建别名索引时投影，见 <see cref="ConfigData.BuildIndex"/>）。</summary>
	internal static IEnumerable<KeyValuePair<ConfigCode, string[]>> All => Map;
}