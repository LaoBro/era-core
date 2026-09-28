using System.Runtime.CompilerServices;
using MinorShift.Emuera.Runtime.Utils.PluginSystem;

// ===== legacy emuera.em 插件兼容层（纯类型转发）=====
//
// 背景：本项目重构后宿主程序集由 `Emuera` 改名为 `EraCore.Core`，而存量游戏（如 eraTW）
// 自带的已编译插件引用的是 `Emuera, Version=1.824.0.0, PublicKeyToken=null`，导致
// Assembly.LoadFrom 后反射抛 ReflectionTypeLoadException。
//
// 本程序集以程序集名 `Emuera` 提供同名的插件 API 类型，但**不定义任何实现**——
// 只把类型转发（TypeForwardedTo）到 EraCore.Core 中的同名类型。这样插件 IL 中的
// (Emuera, PluginSystem.X) 类型标识经转发后与宿主完全一致：
//   - `PluginManifest : PluginManifestAbstract` 的强转成立；
//   - `IPluginMethod` 实现与宿主注册表类型一致；
//   - `methods` / `strValue` 字段按转发后的 MemberRef 解析到 Core 的基类，访问合法。
//
// 低耦合：删除兼容时移除本文件 + 整个 EraCore.Compat.Emuera 项目 + Core 中
// LegacyPluginCompat.cs + PluginManager.LoadPlugins 的调用点 + 配置项即可。
//
// 注意：仅转发 public 类型；internal 类型（如 PluginMethodParameterBuilder）不转发。

[assembly: TypeForwardedTo(typeof(PluginManifestAbstract))]
[assembly: TypeForwardedTo(typeof(IPluginMethod))]
[assembly: TypeForwardedTo(typeof(PluginMethodParameter))]
[assembly: TypeForwardedTo(typeof(PluginManager))]
[assembly: TypeForwardedTo(typeof(PluginAPICharContext))]
[assembly: TypeForwardedTo(typeof(GlobalInt1dWrapper))]
[assembly: TypeForwardedTo(typeof(GlobalString1dWrapper))]
[assembly: TypeForwardedTo(typeof(GlobalConstString1dWrapper))]
[assembly: TypeForwardedTo(typeof(GlobalConstInt1dWrapper))]
[assembly: TypeForwardedTo(typeof(CharInt1dWrapper))]
[assembly: TypeForwardedTo(typeof(CharInt2dWrapper))]
[assembly: TypeForwardedTo(typeof(CharUserdefinedInt1dWrapper))]
[assembly: TypeForwardedTo(typeof(CharStringWrapper))]
[assembly: TypeForwardedTo(typeof(CharString1dWrapper))]