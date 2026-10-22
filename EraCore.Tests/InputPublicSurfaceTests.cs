using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MinorShift.Emuera.Input;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-002 步骤 2：输入轴公共面白名单。
/// <para>
/// 宿主只看门面与纯数据契约；内部输入主机（<c>IInputHost</c> / <c>DefaultInputHost</c> /
/// <c>NullInputHost</c>）、拉取快照与状态应用器必须保持 internal。新增 public 输入类型
/// 会在此处转红 —— 有意公开时显式更新 <see cref="AllowedPublicTypes"/>，不要顺手公开内部件。
/// </para>
/// </summary>
public sealed class InputPublicSurfaceTests
{
    private static readonly string[] AllowedPublicTypes = AxisSurfaceRegistry.Input.AllowedPublicTypes;

    [Fact]
    public void Input_namespace_exposes_only_whitelisted_public_types()
    {
        var actual = AxisSurfaceRegistry.Input.PublicTypes()
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AllowedPublicTypes, actual);
    }

    [Fact]
    public void InputPort_public_surface_is_the_facade_only()
    {
        var declared = typeof(InputPort).GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Concat(typeof(InputPort).GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(p => p.Name))
            .OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(new[] { "Capabilities", "Reset", "Submit" }, declared);
    }

    [Fact]
    public void Public_input_signatures_do_not_leak_internal_types()
    {
        var assembly = typeof(InputPort).Assembly;
        var offenders = new List<string>();

        foreach (var type in AxisSurfaceRegistry.Input.PublicTypes())
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
