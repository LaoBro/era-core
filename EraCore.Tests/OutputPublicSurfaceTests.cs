using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-004 步骤 2：输出轴公共面白名单。
///
/// <para>宿主只看门面与纯数据契据；<c>DisplayState</c> / <c>OutputHub</c> / <c>TurnFrames</c>
/// 与引擎内部类型（<c>ConsoleState</c> / <c>InputType</c> / <c>ConsoleDisplayLine</c> / <c>TurnOp</c>）
/// 必须保持 internal。新增 public 输出类型会在此处转红——有意公开时显式更新
/// <see cref="AllowedPublicTypes"/>，不要顺手公开内部件。</para>
///
/// <para>同时锁住「公共 DTO 只读」：公开类型没有公开构造器、属性没有公开 setter，
/// 外部代码可以读，不能伪造帧或快照。</para>
/// </summary>
public sealed class OutputPublicSurfaceTests
{
    private static readonly string[] AllowedPublicTypes = AxisSurfaceRegistry.Output.AllowedPublicTypes;

    private static IEnumerable<Type> WhitelistedTypes()
        => AxisSurfaceRegistry.Output.PublicTypes();

    [Fact]
    public void Output_namespace_exposes_only_whitelisted_public_types()
    {
        var actual = WhitelistedTypes()
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AllowedPublicTypes, actual);
    }

    [Fact]
    public void OutputPort_public_surface_is_the_facade_only()
    {
        var declared = typeof(OutputPort).GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(new[] { "Snapshot", "Turns" }, declared);
    }

    [Fact]
    public void Public_output_signatures_do_not_leak_internal_types()
    {
        var assembly = typeof(OutputPort).Assembly;
        var offenders = new List<string>();

        foreach (var type in WhitelistedTypes())
        {
            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var member in type.GetMembers(flags))
            {
                foreach (var tb in SignatureTypes(member).SelectMany(Flatten))
                {
                    if (tb.Assembly == assembly && !tb.IsPublic)
                        offenders.Add($"{type.Name}.{member.Name} => {tb.FullName}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// 公共 DTO 只读：没有公开构造器、没有公开可写属性。
    /// 「构造权留在 Core」靠这条闸住——外部宿主拿到 <see cref="Turn"/> 只能读。
    /// </summary>
    [Fact]
    public void Public_output_types_cannot_be_constructed_or_mutated_from_outside()
    {
        var offenders = new List<string>();

        foreach (var type in WhitelistedTypes())
        {
            if (type.IsAbstract)
                continue;   // LineOp：由子类承载构造权

            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                offenders.Add($"{type.Name} has public ctor({string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name))})");

            foreach (var prop in type.GetProperties(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (prop.GetSetMethod(nonPublic: false) != null)
                    offenders.Add($"{type.Name}.{prop.Name} has public setter");
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                offenders.Add($"{type.Name}.{field.Name} is a public mutable field");
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// 引擎内部类型永不公开（spec §5.1）。本条同时是「临时把内部类型改 public 会转红」的守卫。
    /// </summary>
    [Fact]
    public void Engine_types_never_become_public()
    {
        Assert.False(typeof(ConsoleState).IsPublic, "ConsoleState 永不 public");
        Assert.False(typeof(InputType).IsPublic, "InputType 永不 public");
        Assert.False(typeof(ConsoleDisplayLine).IsPublic, "ConsoleDisplayLine 永不 public");
        Assert.False(typeof(TurnOp).IsPublic, "TurnOp 永不 public");

        // 输出轴实现件同样不公开（白名单的补充断言，失败信息更直白）。
        Assert.False(typeof(DisplayState).IsPublic, "DisplayState 是内部实现，经 OutputPort 暴露");
        Assert.False(typeof(OutputHub).IsPublic, "OutputHub 是内部广播中枢");
        Assert.False(typeof(TurnFrames).IsPublic, "TurnFrames 是帧形状内部权威");
    }

    private static IEnumerable<Type> SignatureTypes(MemberInfo member) => member switch
    {
        MethodInfo m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType),
        ConstructorInfo c => c.GetParameters().Select(p => p.ParameterType),
        PropertyInfo p => [p.PropertyType],
        FieldInfo f => [f.FieldType],
        _ => [],
    };

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;
        if (type.HasElementType)
        {
            foreach (var inner in Flatten(type.GetElementType()!))
                yield return inner;
        }
        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments().SelectMany(Flatten))
                yield return arg;
        }
    }
}
