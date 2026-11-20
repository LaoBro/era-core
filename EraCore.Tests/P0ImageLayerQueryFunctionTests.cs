using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 票 38（Tier 2b，Skia 对齐）：EXISTSIMAGELAYER / GETLINEY。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0ImageLayerQueryFunctionTests
{
	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	private sealed class StubViewport(int scrollOffset, int lineHeight, int visibleHeight = 100) : IViewportProvider
	{
		public int ScrollOffset { get; } = scrollOffset;
		public int VisibleHeight { get; } = visibleHeight;
		public int LineHeight { get; } = lineHeight;
	}

	[Theory]
	[InlineData("EXISTSIMAGELAYER")]
	[InlineData("GETLINEY")]
	public void Query_functions_are_snake_only_and_not_restructured(string name)
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		FunctionMethod method = Method(name);
		Assert.Equal(typeof(long), method.ReturnType);
		Assert.False(method.CanRestructure);
		Assert.Contains(name, DialectRegistry.Snake.DeclaredKeys);
		Assert.DoesNotContain(name, DialectRegistry.Baseline.DeclaredKeys);
	}

	// ---------- EXISTSIMAGELAYER ----------

	[Fact]
	public async Task Exists_image_layer_tracks_set_and_clear()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		Assert.Equal(0, Method("EXISTSIMAGELAYER").GetIntValue(h.Exm, Args(new SingleLongTerm(1))));

		h.Console.SetImageLayer("spr", 1, 0, 0, 0, 0, 255, null, false);
		Assert.Equal(1, Method("EXISTSIMAGELAYER").GetIntValue(h.Exm, Args(new SingleLongTerm(1))));
		Assert.Equal(0, Method("EXISTSIMAGELAYER").GetIntValue(h.Exm, Args(new SingleLongTerm(2))));

		// 同深度重复 set 仍是「存在」；clear 单个深度不影响其它。
		h.Console.SetImageLayer("spr2", 2, 0, 0, 0, 0, 255, null, false);
		h.Console.ClearImageLayer(1);
		Assert.Equal(0, Method("EXISTSIMAGELAYER").GetIntValue(h.Exm, Args(new SingleLongTerm(1))));
		Assert.Equal(1, Method("EXISTSIMAGELAYER").GetIntValue(h.Exm, Args(new SingleLongTerm(2))));

		h.Console.ClearImageLayerAll();
		Assert.Equal(0, Method("EXISTSIMAGELAYER").GetIntValue(h.Exm, Args(new SingleLongTerm(2))));
		Assert.Empty(h.Console._state.activeImageLayers);
	}

	// ---------- GETLINEY ----------

	private static void AddLines(LoaderTestHarness h, int count)
	{
		// 初始化会打印若干系统行，这里清空以获得确定的行数。
		h.Console.DisplayLineList.Clear();
		for (int i = 0; i < count; i++)
			h.Console.DisplayLineList.Add(new ConsoleDisplayLine([], isLogical: true, temporary: false));
	}

	[Fact]
	public async Task Getliney_returns_zero_for_bottom_line_and_negative_upwards()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId, viewport: new StubViewport(0, 20));
		await h.InitializeAsync();
		AddLines(h, 5);

		// ScrollOffset=0 → 底行 = 5-0-1 = 4。
		Assert.Equal(0, Method("GETLINEY").GetIntValue(h.Exm, Args(new SingleLongTerm(4))));
		Assert.Equal(-20, Method("GETLINEY").GetIntValue(h.Exm, Args(new SingleLongTerm(3))));
		Assert.Equal(-40, Method("GETLINEY").GetIntValue(h.Exm, Args(new SingleLongTerm(2))));
		// 超出当前行数不截断（向上一行照常计算）。
		Assert.Equal(20, Method("GETLINEY").GetIntValue(h.Exm, Args(new SingleLongTerm(5))));
	}

	[Fact]
	public async Task Getliney_moves_bottom_line_when_scrolled()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId, viewport: new StubViewport(2, 20));
		await h.InitializeAsync();
		AddLines(h, 5);

		// ScrollOffset=2 → 底行 = 5-2-1 = 2。
		Assert.Equal(0, Method("GETLINEY").GetIntValue(h.Exm, Args(new SingleLongTerm(2))));
		Assert.Equal(40, Method("GETLINEY").GetIntValue(h.Exm, Args(new SingleLongTerm(4))));
	}

	[Fact]
	public async Task Getliney_falls_back_to_config_line_height()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId, viewport: new StubViewport(0, 0));
		await h.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.LineHeight).SetValue(30);
		int lineHeight = Config.LineHeight;
		AddLines(h, 3);

		Assert.Equal(0, Method("GETLINEY").GetIntValue(h.Exm, Args(new SingleLongTerm(2))));
		Assert.Equal(-lineHeight, Method("GETLINEY").GetIntValue(h.Exm, Args(new SingleLongTerm(1))));
	}

	[Fact]
	public async Task Getliney_rejects_negative_line_number()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId, viewport: new StubViewport(0, 20));
		await h.InitializeAsync();

		var error = Assert.Throws<CodeEE>(() =>
			Method("GETLINEY").GetIntValue(h.Exm, Args(new SingleLongTerm(-1))));
		Assert.Contains("GETLINEY", error.Message);
	}
}
