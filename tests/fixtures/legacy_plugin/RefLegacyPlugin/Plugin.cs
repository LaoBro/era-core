using MinorShift.Emuera.Runtime.Utils.PluginSystem;
using RefLegacyDepLib;

namespace RefLegacyPlugin;

/// <summary>
/// 参照 1.824 插件（与 eraTW 的 ClassLibrary1.dll 同构）：只依赖
/// <see cref="PluginManifestAbstract"/> / <see cref="IPluginMethod"/> / <see cref="PluginMethodParameter"/>
/// 三个契约类型，并调用一个兄弟依赖 <see cref="Marker"/>。
/// </summary>
public sealed class PluginManifest : PluginManifestAbstract
{
    public PluginManifest()
    {
        methods.Add(new CompatProbeMethod());
    }

    public override string PluginName => "Ref Legacy Plugin";
    public override string PluginDescription => "Self-built legacy-compat reference plugin (AssemblyRef Emuera 1.824).";
    public override string PluginVersion => "1.0";
    public override string PluginAuthor => "era-core tests";
}

public sealed class CompatProbeMethod : IPluginMethod
{
    public string Name => "CompatProbe";

    public string Description => "Reads string arg0 and writes a dependency-derived marker into string arg1.";

    public void Execute(PluginMethodParameter[] args)
    {
        var input = args.Length > 0 ? args[0].strValue : "";
        if (args.Length > 1)
            args[1].strValue = Marker.Wrap(input ?? "");
    }
}