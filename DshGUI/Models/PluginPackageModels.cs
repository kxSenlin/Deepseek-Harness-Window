namespace DshGUI.Models;

/// <summary>插件包导出/导入清单。</summary>
public sealed class PluginPackageManifest
{
    public int FormatVersion { get; set; } = 1;

    public string ProfileName { get; set; } = "";

    public Dictionary<string, string> Dependencies { get; set; } = new(StringComparer.Ordinal);

    public List<string> Bundles { get; set; } = [];

    public List<LocalPluginEntry> LocalPlugins { get; set; } = [];

    public List<string> RemoteDependencies { get; set; } = [];
}

/// <summary>打包进 .dshpkg 的本地插件。</summary>
public sealed class LocalPluginEntry
{
    public string Key { get; set; } = "";

    public string PackageName { get; set; } = "";
}

/// <summary>导入前预览项。</summary>
public sealed class PluginImportPreviewItem
{
    public string Name { get; set; } = "";

    public bool IsDuplicate { get; set; }

    public bool IsSelected { get; set; }

    public bool IsSelectable => !IsDuplicate;
}

/// <summary>导入前预览：哪些已存在会跳过，哪些可勾选新增。</summary>
public sealed class PluginImportPreview
{
    public string ProfileName { get; set; } = "";

    public List<PluginImportPreviewItem> Items { get; set; } = [];

    public List<string> Duplicates { get; set; } = [];

    public List<string> Additions { get; set; } = [];
}
