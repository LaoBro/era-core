using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MinorShift.Emuera.Files;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-005 步骤 2：文件轴公共面白名单。
///
/// <para>宿主只看 <see cref="GameTree"/> / <see cref="GameLibrary"/> / <see cref="IGameDirAccessor"/>
/// 与私有域窄口 <see cref="AppDataPaths"/>；<c>GamePaths</c> / <c>DirectoryLister</c> /
/// <c>DirectoryChildCache</c> / <c>AppDataIo</c> 必须保持 internal。新增 public 文件类型会在此处转红——
/// 有意公开时显式更新 <see cref="AllowedPublicTypes"/>，不要顺手公开内部件。</para>
///
/// <para>同时钉住两件事：门面本身只暴露相对路径读写面（不含选择器）；公开面不泄漏 internal 类型。</para>
/// </summary>
public sealed class FilesPublicSurfaceTests
{
    private static readonly string[] AllowedPublicTypes = AxisSurfaceRegistry.Files.AllowedPublicTypes;

    private static IEnumerable<Type> WhitelistedTypes()
        => AxisSurfaceRegistry.Files.PublicTypes();

    [Fact]
    public void Files_namespace_exposes_only_whitelisted_public_types()
    {
        var actual = WhitelistedTypes()
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AllowedPublicTypes, actual);
    }

    [Fact]
    public void GameTree_public_surface_is_the_relative_path_facade_only()
    {
        var properties = typeof(GameTree)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "CanWrite", "Name", "Root" }, properties);

        var methods = typeof(GameTree)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { "Create", "Delete", "Exists", "ListDirectories", "ListFiles", "OpenRead", "ReadBytes", "ReadText" },
            methods);

        var ctor = Assert.Single(typeof(GameTree).GetConstructors());
        Assert.Equal(
            new[] { typeof(string), typeof(IGameDirAccessor) },
            ctor.GetParameters().Select(p => p.ParameterType).ToArray());
    }

    [Fact]
    public void Directory_picker_is_not_part_of_the_io_contract()
    {
        Assert.DoesNotContain(
            typeof(IGameDirAccessor).GetMethods(),
            m => m.Name.Contains("PickDirectory", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(FileSystemGameDirAccessor).GetMethods(),
            m => m.Name.Contains("PickDirectory", StringComparison.Ordinal));
    }

    [Fact]
    public void Public_file_signatures_do_not_leak_internal_types()
    {
        var assembly = typeof(GameTree).Assembly;
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
    public void Internal_file_axis_types_stay_internal()
    {
        Assert.False(typeof(GamePaths).IsPublic, "GamePaths 是引擎内部路径代数");
        Assert.False(typeof(GamePathValidationException).IsPublic, "校验异常经宿主入口翻译，不直接暴露");
        Assert.False(typeof(DirectoryLister).IsPublic, "DirectoryLister 是 UI 目录浏览的内部工具");
        Assert.False(typeof(DirectoryListResult).IsPublic, "DirectoryListResult 是内部返回形状");
        Assert.False(typeof(DirectoryChildCache).IsPublic, "DirectoryChildCache 是 SAF 实现细节");
        Assert.False(typeof(AppDataIo).IsPublic, "AppDataIo 是私有域窄口内部件");
        Assert.False(typeof(AppDataPath).IsPublic, "AppDataPath 是私有域路径凭据");
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
