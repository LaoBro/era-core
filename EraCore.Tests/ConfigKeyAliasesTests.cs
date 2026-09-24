using System;
using System.Collections.Generic;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 配置键别名表（<see cref="ConfigKeyAliases"/>）的自校验。表当前为空——等上游简体中文分支
/// （如 Emuera1821 cn）源码到位后逐项填入。本测试保证"一填就守"：
/// <list type="bullet">
/// <item>别名不得为空（空串会污染别名索引）；</item>
/// <item>别名不得与任何内建键名（Name/Text/EngText）相同——那种情况下内建命中会抢先，
/// 别名等于白填，且会掩盖真正写错的映射；</item>
/// <item>同一别名不得被两个 ConfigCode 同时声明（先到先得会静默吞掉后者）。</item>
/// </list>
/// </summary>
[Collection("LoaderTests")]
public class ConfigKeyAliasesTests
{
	[Fact]
	public void Alias_table_has_no_empty_or_colliding_entries()
	{
		using var globals = GlobalStatic.OpenScope(new ConfigData());
		var config = ConfigData.Current!;

		// 内建键名集合（不含别名索引——别名与自身比较无意义）
		var builtinKeys = new HashSet<string>(StringComparer.Ordinal);
		foreach (ConfigCode code in Enum.GetValues<ConfigCode>())
		{
			AConfigItem item = config.GetConfigItem(code);
			if (item == null)
				continue;
			builtinKeys.Add(item.Name);
			builtinKeys.Add(item.Text);
			builtinKeys.Add(item.EngText);
		}

		var declared = new Dictionary<string, ConfigCode>(StringComparer.Ordinal);
		foreach (var (code, aliases) in ConfigKeyAliases.All)
		{
			foreach (string alias in aliases)
			{
				Assert.False(string.IsNullOrWhiteSpace(alias), $"{code} 声明了空别名");

				// 与 ConfigData.GetConfigItem 同口径归一化（Trim + ToUpper）
				string normalized = alias.Trim().ToUpper();

				Assert.False(builtinKeys.Contains(normalized),
					$"别名 '{alias}'（{code}）与内建键名冲突——内建命中会抢先，别名不会生效");

				Assert.False(declared.TryGetValue(normalized, out ConfigCode owner),
					$"别名 '{alias}' 同时被 {code} 与 {owner} 声明");
				declared[normalized] = code;
			}
		}
	}
}