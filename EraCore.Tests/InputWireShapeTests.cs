using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.UI.Game;
using Xunit;
using HttpInput = MinorShift.Emuera.Server.HttpRouteDispatcher.HttpInput;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 上行输入状态的 <b>wire 形状守护</b>（候选 5 深化的收尾）。
/// <para>
/// 上行状态原本有四处形状真相：
/// <list type="number">
/// <item>前端 <c>InputExtra</c>（<c>EraCore.Web/src/stores/connection.ts</c>）—— TS，不在本仓构建内，靠人工对齐。</item>
/// <item><c>HttpInput</c>（HTTP body POCO，反射/源生成反序列化）—— 见本文件。</item>
/// <item>手拼的 <c>JsonObject</c> —— <b>已删除</b>，改为序列化 <see cref="JsonlCommand"/>。</item>
/// <item><see cref="JsonlCommand"/>（协议帧 record，源生成）—— 见本文件。</item>
/// </list>
/// 剩下 ② 与 ④ 无法物理合并（分属两种序列化机制，且 HTTP body 多一个传输层的 <c>token</c>），
/// 故用<b>契约测试</b>守住二者不漂移，并以 <see cref="InputStateEvent"/> 作为共同终点。
/// </para>
/// </summary>
public sealed class InputWireShapeTests
{
    private static readonly string[] StateFields = { "hoverValue", "keys", "geometry", "focus" };

    private static Dictionary<string, Type> StateShape(Type t)
        => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
             .Where(p => StateFields.Contains(p.Name))
             .ToDictionary(p => p.Name, p => p.PropertyType, StringComparer.Ordinal);

    /// <summary>
    /// 两个 wire 入口必须声明<b>同名同类型</b>的状态字段。
    /// 加字段时两边一起加 —— 只改一边，这里转红（而不是等前端上报了一个被静默丢弃的字段）。
    /// </summary>
    [Fact]
    public void Both_wire_entries_declare_the_same_state_fields()
    {
        var jsonl = StateShape(typeof(JsonlCommand));
        var http = StateShape(typeof(HttpInput));

        Assert.Equal(
            StateFields.OrderBy(k => k, StringComparer.Ordinal),
            jsonl.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var name in StateFields)
        {
            Assert.True(http.TryGetValue(name, out var httpType), $"HttpInput 缺少状态字段 `{name}`");
            Assert.Equal(jsonl[name], httpType);
        }
    }

    /// <summary>
    /// 两边从同一组字段出发必须收敛到<b>同一个</b> <see cref="InputStateEvent"/> ——
    /// 这是「进 Core 第一步就单点化」的可执行断言。
    /// </summary>
    [Fact]
    public void Both_wire_entries_converge_to_the_same_state_event()
    {
        var keys = new[] { new InputKeyEvent(65, true) };

        var fromJsonl = new JsonlCommand("input", "1", "42", keys, new InputGeometry(3, 4), false).ToInputState();
        var fromHttp = new HttpInput
        {
            value = "1",
            hoverValue = "42",
            keys = keys,
            geometry = new InputGeometry(3, 4),
            focus = false,
        }.ToInputState();

        Assert.Equal(fromJsonl.HoverValue, fromHttp.HoverValue);
        Assert.Equal(fromJsonl.Geometry, fromHttp.Geometry);
        Assert.Equal(fromJsonl.Focus, fromHttp.Focus);
        Assert.Equal(fromJsonl.Keys!.Length, fromHttp.Keys!.Length);
        Assert.Equal(fromJsonl.Keys[0], fromHttp.Keys[0]);
        Assert.Equal(fromJsonl.HasAny, fromHttp.HasAny);
        Assert.True(fromHttp.HasAny);
    }

    /// <summary>
    /// <c>value</c> 与 <c>token</c> 都不属于输入状态：<c>value</c> 走派发，
    /// <c>token</c> 是 HTTP 传输层（不进协议帧）。状态字段集合不得把它们混进来。
    /// </summary>
    [Fact]
    public void State_event_carries_no_value_or_token()
    {
        Assert.DoesNotContain("value", StateFields);
        Assert.DoesNotContain("token", StateFields);
        Assert.False(new InputStateEvent().HasAny);
        Assert.False(new HttpInput { value = "1" }.HasInputState);
        Assert.True(new HttpInput { token = "t" }.HasInputState == false);
    }
}
