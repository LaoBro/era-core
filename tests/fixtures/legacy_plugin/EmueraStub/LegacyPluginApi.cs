using System.Collections.Generic;

// 复刻原版 Emuera 1.824 的 legacy 插件契约类型（仅 public 形状，供测试插件编译期引用）。
// 形状必须与 EraCore.Core 中的同名类型逐字节一致（字段/虚方法/接口签名），
// 否则测试插件在运行时可能加载成功但调用失败——那正是本测试要覆盖的兼容面。
namespace MinorShift.Emuera.Runtime.Utils.PluginSystem;

public class PluginMethodParameter
{
    public PluginMethodParameter(string initialValue)
    {
        isString = true;
        strValue = initialValue;
    }

    public PluginMethodParameter(long initialValue)
    {
        isString = false;
        intValue = initialValue;
    }

    public bool isString;
    public string strValue = null!;
    public long intValue;
}

public interface IPluginMethod
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract void Execute(PluginMethodParameter[] args);
}

public abstract class PluginManifestAbstract
{
    public abstract string PluginName { get; }
    public abstract string PluginDescription { get; }
    public abstract string PluginVersion { get; }
    public abstract string PluginAuthor { get; }

    public PluginManifestAbstract() { }

    public List<IPluginMethod> GetPluginMethods()
    {
        return methods;
    }

    protected List<IPluginMethod> methods = [];
}