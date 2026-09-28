namespace RefLegacyDepLib;

/// <summary>兄弟依赖：仅当宿主能从插件目录解析出本程序集时，标记才会带上 <see cref="Tag"/>。</summary>
public static class Marker
{
    public const string Tag = "SIDE_DEP_OK";

    public static string Wrap(string value) => $"[COMPAT_OK:{value}|{Tag}]";
}