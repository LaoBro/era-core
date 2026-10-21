using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MinorShift.Emuera.Sessions;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-006 步骤 3：会话轴公共面白名单。
///
/// <para>宿主只看组装根 <see cref="CoreSession"/> 及其参数/结果/状态类型；<c>SessionRegistry</c> /
/// <c>HttpSessionIO</c> / <c>TurnDelivery</c> / <c>Controller</c> / <c>GameLoopComposer</c>
/// 等装配与交付实现必须保持 internal。新增 public 会话类型会在此处转红——有意公开时显式更新
/// <see cref="AllowedPublicTypes"/>，不要顺手公开内部件。</para>
///
/// <para>同时锁两件事：<see cref="CoreSession"/> 的公共面只有「三轴 + 生命周期」；
/// 验收用的公共 API 测试不得回退到 internal 类型（否则「只依赖三轴」的证明失效）。</para>
/// </summary>
public sealed class SessionsPublicSurfaceTests
{
    private const string SessionsNamespace = "MinorShift.Emuera.Sessions";

    private static readonly string[] AllowedPublicTypes =
    [
        "CoreSession",
        "CoreSessionOptions",
        "CoreSessionStartResult",
        "CoreSessionState",
    ];

    private static IEnumerable<Type> WhitelistedTypes()
        => typeof(CoreSession).Assembly.GetTypes()
            .Where(t => t.Namespace == SessionsNamespace && t.IsPublic);

    [Fact]
    public void Sessions_namespace_exposes_only_whitelisted_public_types()
    {
        var actual = WhitelistedTypes()
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AllowedPublicTypes, actual);
    }

    [Fact]
    public void CoreSession_public_surface_is_axes_plus_lifecycle()
    {
        var properties = typeof(CoreSession)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { "CreatedAt", "Files", "Id", "Input", "LastError", "Output", "State" },
            properties);

        var methods = typeof(CoreSession)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "Dispose", "StartAsync", "StopAsync" }, methods);

        var ctor = Assert.Single(typeof(CoreSession).GetConstructors());
        Assert.Equal(
            new[] { typeof(CoreSessionOptions) },
            ctor.GetParameters().Select(p => p.ParameterType).ToArray());
    }

    [Fact]
    public void Public_session_signatures_do_not_leak_internal_types()
    {
        var assembly = typeof(CoreSession).Assembly;
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

    [Fact]
    public void Internal_session_types_stay_internal()
    {
        Assert.False(typeof(SessionRegistry).IsPublic, "SessionRegistry 是 Server 侧会话表实现");
        Assert.False(typeof(HttpSessionIO).IsPublic, "HttpSessionIO 是双写交付实现");
        Assert.False(typeof(SessionIO).IsPublic, "SessionIO 是 IO 契约内部件");
        Assert.False(typeof(TurnDelivery).IsPublic, "TurnDelivery 是长轮询/控制权交付编排");
        Assert.False(typeof(Controller).IsPublic, "Controller 是控制权租约实现");
        Assert.False(typeof(BoundedLoopWait).IsPublic, "BoundedLoopWait 是拆除等待深模块");
        Assert.False(typeof(GameLoopComposer).IsPublic, "GameLoopComposer 是循环装配内部件");
        Assert.False(
            typeof(CoreSession).Assembly.GetType(SessionsNamespace + ".CoreSessionProtocolFactory")!.IsPublic,
            "协议工厂是宿主接入接缝，不进入公共面");
    }

    /// <summary>
    /// 验收测试必须只经公共 API 驱动会话。源码守卫兜住「测试为了方便又摸回 internal」的退化，
    /// 因为反射看不到 <c>InternalsVisibleTo</c> 的用法。
    /// </summary>
    [Fact]
    public void Public_api_acceptance_test_uses_only_public_surfaces()
    {
        var path = Path.Combine(
            FindRepositoryRoot(), "EraCore.Tests", "CoreSessionPublicApiTests.cs");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("InternalsVisibleTo", source);

        string[] forbidden =
        [
            "OutputHub", "DisplayState", "DefaultInputHost", "HttpSessionIO", "SessionRegistry",
            "TurnDelivery", "Controller", "AgentJsonlProtocol", "AgentProtocolBase",
            "GameLoopComposer", "ITerminalSetup", "EmueraConsole", "TurnFrames", "GamePaths",
        ];
        foreach (var name in forbidden)
            Assert.DoesNotContain(name, source);

        string[] allowedNamespaces =
        [
            "MinorShift.Emuera.Files",
            "MinorShift.Emuera.Input",
            "MinorShift.Emuera.Output",
            "MinorShift.Emuera.Sessions",
        ];
        var emueraUsings = source
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("using MinorShift.Emuera.", StringComparison.Ordinal))
            .Select(l => l["using ".Length..].TrimEnd(';', '\r'))
            .ToArray();
        Assert.NotEmpty(emueraUsings);
        foreach (var ns in emueraUsings)
            Assert.Contains(ns, allowedNamespaces);
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EraCore.sln")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
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
