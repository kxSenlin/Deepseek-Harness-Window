using System.Text.Json;

namespace DshGUI.Services;

/// <summary>跨运行保存的首次盘点基线。</summary>
internal sealed class PluginInventoryBaseline
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DshGUI", "plugin-inventory-baseline.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Dictionary<string, PluginInventoryBaselineProfile> Profiles { get; set; } = new(StringComparer.Ordinal);

    public static PluginInventoryBaseline Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new PluginInventoryBaseline();
            return JsonSerializer.Deserialize<PluginInventoryBaseline>(
                File.ReadAllText(FilePath), JsonOptions) ?? new PluginInventoryBaseline();
        }
        catch
        {
            return new PluginInventoryBaseline();
        }
    }

    public PluginInventoryBaselineProfile GetOrCreate(string profileName, PluginProfileSnapshot snapshot)
    {
        if (Profiles.TryGetValue(profileName, out var existing))
            return existing;

        var created = new PluginInventoryBaselineProfile
        {
            Rows = snapshot.Rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal),
            Packages = snapshot.Packages.Select(p => p.Name).ToHashSet(StringComparer.Ordinal),
        };
        Profiles[profileName] = created;
        Save();
        return created;
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            DshFileSystem.WriteAllTextAtomic(
                FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 基线保存失败只影响严格确认提示，不影响插件操作。
        }
    }
}

internal sealed class PluginInventoryBaselineProfile
{
    public HashSet<string> Rows { get; set; } = new(StringComparer.Ordinal);

    public HashSet<string> Packages { get; set; } = new(StringComparer.Ordinal);
}
