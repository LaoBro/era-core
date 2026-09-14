using System.Collections.Generic;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// erb-loader-parallel 回归：并行未调用清扫（RunUncalledSweepParallel）工作线程解析函数体
/// `#DIM STATIC` → AddPrivateVariable → CreatePrivateVariable 并发追加 userDefinedStaticVarList。
/// 普通 List 非线程安全——补锁（_staticVarListLock）前并发追加可撕裂内部数组（计数与容量不一致），
/// 加载后 RESETDATA → SetDefaultLocalValue 的 foreach 迭代读到撕裂槽位 → 间歇性 NRE。
/// 本测试以并发 Add + 迭代冒烟验证锁：无锁下大概率当场抛 IndexOutOfRange/NRE 或迭代期损坏，有锁恒绿。
/// </summary>
[Collection("LoaderTests")]
public class VariableDataConcurrencyTests
{
	[Fact]
	public async Task CreatePrivateVariable_concurrent_static_adds_then_SetDefaultLocalValue_is_clean()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();

		const int threads = 8;
		const int perThread = 250;
		var varData = h.VEvaluator.VariableData;

		await Task.Run(() =>
		{
			var workers = new List<Task>();
			for (int t = 0; t < threads; t++)
			{
				int tid = t;
				workers.Add(Task.Run(() =>
				{
					for (int i = 0; i < perThread; i++)
					{
						varData.CreatePrivateVariable(new UserDefinedVariableData
						{
							Name = $"TESTVAR_T{tid}_{i}",
							Dimension = 1,
							Lengths = [1],
							Static = true,
							Private = true,
						});
					}
				}));
			}
			Task.WaitAll([.. workers]);
		});

		Assert.Equal(threads * perThread, varData.UserDefinedStaticVarCountForTests);

		// 迭代即验证：撕裂的 List 在 foreach 中会抛 NRE / 越界
		varData.SetDefaultLocalValue();
	}
}
