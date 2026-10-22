using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MinorShift.Emuera.Files;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Sessions;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-010：公共面与目录归属的聚合守卫。
///
/// <para>四个模块的白名单集中在 <see cref="AxisSurfaceRegistry"/>，由各自的
/// <c>*PublicSurfaceTests</c> 与本类共同引用。本类补上跨轴检查：白名单齐全、公共签名不泄漏
/// 引擎内部类型、轴命名空间只出现在自己的目录、游戏内容直连 IO 只在文件轴或登记豁免、
/// 公共接入探针不依赖 <c>InternalsVisibleTo</c>。</para>
/// </summary>
public sealed class ArchitectureAggregateGuardTests
{
    private static readonly Type[] AxisGuardTests =
    [
        typeof(InputPublicSurfaceTests),
        typeof(OutputPublicSurfaceTests),
        typeof(FilesPublicSurfaceTests),
        typeof(SessionsPublicSurfaceTests),
    ];

    private static readonly Regex NamespaceDeclaration = new(
        @"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void Four_axis_whitelists_are_declared_and_guarded()
    {
        Assert.Equal(4, AxisSurfaceRegistry.All.Count);
        Assert.Equal(4, AxisSurfaceRegistry.All.Select(a => a.Namespace).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, AxisSurfaceRegistry.All.Select(a => a.Directory).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, AxisGuardTests.Length);

        foreach (var axis in AxisSurfaceRegistry.All)
        {
            Assert.NotEmpty(axis.AllowedPublicTypes);
            Assert.Equal(axis.Namespace, axis.Facade.Namespace);
            Assert.True(axis.Facade.IsPublic, $"{axis.Facade.FullName} must be public");
        }

        foreach (var guard in AxisGuardTests)
        {
            Assert.Contains(
                guard.GetMethods(BindingFlags.Public | BindingFlags.Instance),
                m => m.GetCustomAttribute<FactAttribute>() != null
                     && m.Name.EndsWith("_exposes_only_whitelisted_public_types", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Public_surface_of_each_axis_matches_its_whitelist()
    {
        var violations = new List<string>();
        foreach (var axis in AxisSurfaceRegistry.All)
        {
            var actual = axis.PublicTypes()
                .Select(t => t.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            var expected = axis.AllowedPublicTypes
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
                violations.Add($"{axis.Axis}: actual=[{string.Join(", ", actual)}] expected=[{string.Join(", ", expected)}]");
        }

        Assert.True(violations.Count == 0,
            "Axis public surface drifted from its whitelist:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void Public_axis_signatures_never_reference_internal_core_types()
    {
        var assembly = typeof(InputPort).Assembly;
        var offenders = new List<string>();

        foreach (var axis in AxisSurfaceRegistry.All)
        foreach (var type in axis.PublicTypes())
        foreach (var referenced in DeclaredSignatureTypes(type).SelectMany(Flatten))
        {
            if (referenced.Assembly == assembly && !referenced.IsPublic)
                offenders.Add($"{type.FullName} => {referenced.FullName}");
        }

        Assert.True(offenders.Count == 0,
            "Public axis signatures leak internal engine types:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Engine_internal_types_stay_internal()
    {
        Type[] engineInternals =
        [
            typeof(ConsoleState),
            typeof(InputType),
            typeof(ConsoleDisplayLine),
            typeof(TurnOp),
            typeof(DisplayState),
            typeof(OutputHub),
            typeof(TurnFrames),
            typeof(GamePaths),
            typeof(SessionRegistry),
            typeof(HttpSessionIO),
            typeof(GameLoopComposer),
        ];

        var offenders = engineInternals
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .Select(t => t.FullName)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Engine internals must never become public: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Axis_namespaces_live_only_in_their_own_directories()
    {
        var coreDir = Path.Combine(GameDirAccessorIoPolicyTests.FindRepositoryRoot(), "EraCore.Core");
        var violations = new List<string>();

        foreach (var relativePath in GameDirAccessorIoPolicyTests.EnumerateSourceFiles(coreDir))
        {
            var namespaces = NamespaceDeclaration
                .Matches(File.ReadAllText(Path.Combine(coreDir, relativePath)))
                .Select(m => m.Groups[1].Value)
                .ToArray();

            foreach (var ns in namespaces)
            foreach (var axis in AxisSurfaceRegistry.All)
            {
                if (IsAxisNamespace(ns, axis) &&
                    !relativePath.StartsWith(axis.Directory + "/", StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}: {ns} must live under {axis.Directory}/");
                }
            }

            foreach (var axis in AxisSurfaceRegistry.All)
            {
                if (relativePath.StartsWith(axis.Directory + "/", StringComparison.Ordinal) &&
                    !namespaces.Any(ns => IsAxisNamespace(ns, axis)))
                {
                    violations.Add($"{relativePath}: file under {axis.Directory}/ declares no {axis.Namespace} namespace");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Axis types escaped their module directory:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void Direct_game_content_io_stays_in_the_file_axis_or_registered_exemptions()
    {
        var coreDir = Path.Combine(GameDirAccessorIoPolicyTests.FindRepositoryRoot(), "EraCore.Core");
        var violations = new List<string>();

        foreach (var relativePath in GameDirAccessorIoPolicyTests.EnumerateSourceFiles(coreDir))
        {
            var count = GameDirAccessorIoPolicyTests.CountDirectIo(Path.Combine(coreDir, relativePath));
            if (count == 0)
                continue;
            if (relativePath.StartsWith("Files/", StringComparison.Ordinal))
                continue;
            if (GameDirAccessorIoPolicyTests.IsRegisteredDirectIoExemption(relativePath))
                continue;

            violations.Add($"{relativePath}: {count} direct I/O call(s) outside Files/ and not in the registered exemption list");
        }

        Assert.True(violations.Count == 0,
            "Direct game-content I/O escaped the file axis:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void Public_api_probe_project_has_no_internal_access()
    {
        var root = GameDirAccessorIoPolicyTests.FindRepositoryRoot();
        var projectDir = Path.Combine(root, "EraCore.PublicApi.Tests");
        var projectPath = Path.Combine(projectDir, "EraCore.PublicApi.Tests.csproj");
        Assert.True(File.Exists(projectPath), $"{projectPath} is missing — the public-API probe project must exist");

        var project = XDocument.Load(projectPath);
        Assert.DoesNotContain(project.Descendants(), e => e.Name.LocalName == "InternalsVisibleTo");

        var references = project.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => ((string?)e.Attribute("Include"))?.Replace('\\', '/'))
            .Where(p => p is not null)
            .Select(p => p!)
            .ToArray();
        Assert.Equal(new[] { "../EraCore.Core/EraCore.Core.csproj" }, references);

        var coreProject = File.ReadAllText(Path.Combine(root, "EraCore.Core", "EraCore.Core.csproj"));
        Assert.DoesNotContain("EraCore.PublicApi.Tests", coreProject);

        var probeSource = File.ReadAllText(Path.Combine(projectDir, "PublicApiAccessTests.cs"));
        string[] forbidden =
        [
            "OutputHub", "DisplayState", "DefaultInputHost", "HttpSessionIO",
            "SessionRegistry", "TurnDelivery", "Controller", "GameLoopComposer", "EmueraConsole",
            "TurnFrames", "GamePaths", "RootScope",
        ];
        foreach (var name in forbidden)
            Assert.DoesNotContain(name, probeSource);

        var axisNamespaces = AxisSurfaceRegistry.All.Select(a => a.Namespace).ToArray();
        var emueraUsings = probeSource
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("using MinorShift.Emuera.", StringComparison.Ordinal))
            .Select(l => l["using ".Length..].TrimEnd(';', '\r'))
            .ToArray();
        Assert.NotEmpty(emueraUsings);
        foreach (var ns in emueraUsings)
            Assert.Contains(ns, axisNamespaces);
    }

    private static bool IsAxisNamespace(string ns, AxisSurface axis)
        => ns == axis.Namespace || ns.StartsWith(axis.Namespace + ".", StringComparison.Ordinal);

    private static IEnumerable<Type> DeclaredSignatureTypes(Type type)
    {
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var member in type.GetMembers(flags))
        {
            switch (member)
            {
                case MethodInfo method:
                    foreach (var parameter in method.GetParameters())
                        yield return parameter.ParameterType;
                    yield return method.ReturnType;
                    break;
                case ConstructorInfo constructor:
                    foreach (var parameter in constructor.GetParameters())
                        yield return parameter.ParameterType;
                    break;
                case PropertyInfo property:
                    yield return property.PropertyType;
                    break;
                case FieldInfo field:
                    yield return field.FieldType;
                    break;
                case EventInfo @event:
                    yield return @event.EventHandlerType!;
                    break;
            }
        }

        if (type.BaseType != null)
            yield return type.BaseType;
        foreach (var @interface in type.GetInterfaces())
            yield return @interface;
    }

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
