using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// erb-bounded-lifecycle（handoff §8.1 调度生命周期子集）：有界滑动窗口调度的行为锚点。
/// 失败点/延迟/observer 经 ErbLoader 的 Test* 测试接缝注入，不依赖真实线程调度的偶然性。
///
/// 覆盖：首错后尾任务收束（start 的 worker 必须在返回前 end）、窗口上界（并发 worker 数
/// 不超过窗口）、外部取消传播、多文件 #DIM/#DIMS 压力（privateDimList 锁）、异常按序回放一次。
/// </summary>
[Collection("LoaderTests")]
public class ErbLoaderBoundedSchedulerTests
{
	private static ErbLoader NewLoader(LoaderTestHarness h, List<LogicalLine?>? spy = null)
		=> new(h.Console, h.Exm, h.IdDic, h.Env, line => spy?.Add(line));

	/// <summary>
	/// 首错收束：低索引文件注入异常、高索引文件进入可控阻塞时——LoadErbDir 抛出原异常，
	/// 且所有已启动 worker（含被取消的阻塞者）都在返回前 end；未启动文件永不 start。
	/// 旧实现此处 using SemaphoreSlim 先 Dispose，尾任务在已释放 gate 上无人观察地炸。
	/// </summary>
	[Fact]
	public async Task First_error_awaits_all_started_workers_before_returning()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("A00.ERB", "@FUNC_A00\nRETURN\n");
		for (int i = 1; i <= 5; i++)
			h.WriteErb($"A0{i}.ERB", $"@FUNC_A0{i}\nRETURN\n");
		await h.PreloadAsync();

		var events = new ConcurrentQueue<string>();
		var loader = NewLoader(h);
		loader.TestWindowOverride = 2;
		loader.TestGenerateFaultHook = name => name == "A00.ERB" ? new InvalidOperationException("boom-A00") : null;
		// A01 进入可控阻塞；token 感知——首错收束取消后立即退出（不依赖 Sleep 时长猜测）
		loader.TestGenerateGateHook = async (name, ct) =>
		{
			if (name != "A01.ERB")
				return;
			events.Enqueue("blocked:A01.ERB");
			await Task.Delay(Timeout.Infinite, ct);
		};
		loader.TestWorkerObserver = (phase, name) => events.Enqueue($"{phase}:{name}");

		// LoadErbDir 现有失败路径：捕获异常 → PrintError(类型:消息) → 返回 false
		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, new LabelDictionary());
		Assert.False(ok, "first error must fail the load");
		var lines = h.Console.DisplayLineList.Select(l => l?.ToString() ?? "").ToList();
		Assert.Contains(lines, s => s.Contains("InvalidOperationException") && s.Contains("boom-A00")); // 原异常类型/消息保留
		events.Enqueue("returned");
		var seq = events.ToList();
		var returnedIdx = seq.IndexOf("returned");
		Assert.True(returnedIdx >= 0);

		// 只有窗口内的 A00/A01 被 start（A02-A05 永不启动——旧实现全量 Task 数组则会全部创建）；
		// 两者由线程池并发启动，start 先后不保证，断言集合
		var started = seq.Where(s => s.StartsWith("start:")).OrderBy(s => s).ToList();
		Assert.Equal(["start:A00.ERB", "start:A01.ERB"], started);

		// 每个已启动 worker 的 end 都先于返回（尾任务收束不变式）
		foreach (var name in new[] { "A00.ERB", "A01.ERB" })
		{
			var endIdx = seq.IndexOf($"end:{name}");
			Assert.True(endIdx >= 0 && endIdx < returnedIdx, $"worker {name} must end before LoadErbDir returns");
		}
		// A01 是被取消收束的（cancel 事件在 end 之前）
		var cancelIdx = seq.IndexOf("cancel:A01.ERB");
		Assert.True(cancelIdx >= 0 && cancelIdx < seq.IndexOf("end:A01.ERB"), "blocked worker must be canceled before ending");
	}

	/// <summary>窗口上界：并发运行中的 worker 数永不超窗口宽度（observer 计数断言），且全部文件加载成功。</summary>
	[Fact]
	public async Task Concurrent_workers_never_exceed_window()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		for (int i = 0; i < 12; i++)
			h.WriteErb($"F{i:D2}.ERB", $"@FUNC_{i}\nRETURN\n");
		await h.PreloadAsync();

		const int window = 3;
		int active = 0, peak = 0;
		var loader = NewLoader(h);
		loader.TestWindowOverride = window;
		// gate hook 让每个 worker 在 start 观察点后驻留一拍，使峰值可观测
		loader.TestGenerateGateHook = (_, ct) => Task.Delay(30, ct);
		loader.TestWorkerObserver = (phase, _) =>
		{
			switch (phase)
			{
				case "start":
					Assert.True(Interlocked.Increment(ref active) <= window, "active workers exceeded window");
					peak = Math.Max(peak, Volatile.Read(ref active));
					break;
				case "end":
				case "cancel":
					Interlocked.Decrement(ref active);
					break;
			}
		};
		var labelDic = new LabelDictionary();
		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		Assert.True(ok);
		Assert.True(labelDic.Count >= 12);
		Assert.True(peak <= window);
		Assert.Equal(0, Volatile.Read(ref active)); // 全部收束，无泄漏 worker
	}

	/// <summary>外部取消：token 触发后阻塞中的 worker 以 cancel 收束，LoadErbDir 走失败返回，不悬挂。</summary>
	[Fact]
	public async Task External_cancel_unblocks_workers_and_returns()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("F00.ERB", "@FUNC_0\nRETURN\n");
		h.WriteErb("F01.ERB", "@FUNC_1\nRETURN\n");
		await h.PreloadAsync();

		using var cts = new CancellationTokenSource();
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var loader = NewLoader(h);
		loader.TestWindowOverride = 1;
		loader.TestGenerateGateHook = async (_, ct) =>
		{
			started.TrySetResult();
			await Task.Delay(Timeout.Infinite, ct);
		};
		var loadTask = loader.LoadErbDir(h.ErbDir, displayReport: false, new LabelDictionary(), cts.Token);

		// 超时抛 TimeoutException 使测试失败
		await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
		cts.Cancel();

		// 取消 → 首错（OCE）按序浮出 → LoadErbDir 现有失败路径 catch → 返回 false
		Assert.False(await loadTask.WaitAsync(TimeSpan.FromSeconds(10)));
	}

	/// <summary>
	/// 多文件大量 #DIM/#DIMS 压力（privateDimList 并发 Add 竞态面）：反复并行加载不抛
	/// ArgumentOutOfRangeException/IndexOutOfRange 等列表损坏异常，标签装载完整；与串行签名一致。
	/// </summary>
	[Fact]
	public async Task Many_files_with_many_dims_parallel_load_survives_repeated_rounds()
	{
		const int files = 40, dimsPerFile = 25;
		static List<string> Signature(LabelDictionary dic) =>
			dic.GetAllLabels(false).OrderBy(l => l.Index).Select(l => l.LabelName).ToList();

		List<string> ExpectedSignature() => Enumerable.Range(0, files).Select(i => $"FUNC_{i:D2}").ToList();

		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		for (int i = 0; i < files; i++)
		{
			var lines = new List<string> { $"@FUNC_{i:D2}" };
			for (int d = 0; d < dimsPerFile; d++)
				lines.Add($"#DIM LOCALVAR_{i:D2}_{d:D2}");
			lines.Add("RETURN");
			h.WriteErb($"D{i:D2}.ERB", string.Join("\n", lines) + "\n");
		}
		await h.PreloadAsync();

		// 3 轮并行加载（默认窗口），触发并发 Add 的损坏窗口
		for (int round = 0; round < 3; round++)
		{
			var labelDic = new LabelDictionary();
			var ok = await NewLoader(h).LoadErbDir(h.ErbDir, displayReport: false, labelDic);
			Assert.True(ok, $"parallel round {round} failed");
			Assert.Equal(ExpectedSignature(), Signature(labelDic));
		}

		// 串行对照：签名一致
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
		var serialDic = new LabelDictionary();
		var serialOk = await new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line)
			.LoadErbDir(h.ErbDir, displayReport: false, serialDic);
		Assert.True(serialOk);
		Assert.Equal(ExpectedSignature(), Signature(serialDic));
	}

	/// <summary>异常按序回放一次：生成中途已产生的警告在首错浮出时恰好回放一次，原异常保留。</summary>
	[Fact]
	public async Task Partial_artifact_warning_replayed_exactly_once_on_first_error()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		// A00 内 $LAB 重複 → 生成期产生缓冲警告；随后注入异常 → 合并序首位回放
		h.WriteErb("A00.ERB", "@FOO\n$LAB\n$LAB\nRETURN\n");
		await h.PreloadAsync();

		var loader = NewLoader(h);
		loader.TestGenerateFaultHook = _ => new InvalidOperationException("boom-after-generate");

		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, new LabelDictionary());
		Assert.False(ok);

		var count = h.Console.DisplayLineList.Count(l => l?.ToString()?.Contains("既に同じ関数内") == true);
		Assert.Equal(1, count);
		var lines = h.Console.DisplayLineList.Select(l => l?.ToString() ?? "").ToList();
		Assert.Contains(lines, s => s.Contains("InvalidOperationException") && s.Contains("boom-after-generate"));
	}
}
