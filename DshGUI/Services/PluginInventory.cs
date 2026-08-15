using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DshGUI.Models;

namespace DshGUI.Services;

/// <summary>bundle 层解析结果。</summary>
public sealed class BundleLayerInfo
{
    public string Name { get; init; } = "";

    public string PackageDirectory { get; init; } = "";

    public string PatchPath { get; init; } = "";

    public bool IsBuiltIn { get; init; }
}

/// <summary>已安装包（package.json dependencies）。</summary>
public sealed class InstalledPackageItem
{
    public string Name { get; init; } = "";

    public string Spec { get; init; } = "";

    public bool IsBundleListed { get; init; }

    public bool HasPatchRows { get; set; }

    public string EntityPath { get; init; } = "";

    public bool IsFileOrLink => Spec.StartsWith("file:", StringComparison.Ordinal)
        || Spec.StartsWith("link:", StringComparison.Ordinal);
}

/// <summary>Profile 插件行盘点结果。</summary>
public sealed class PluginProfileSnapshot
{
    public string ProfileName { get; init; } = "";

    public string ProfileDirectory { get; init; } = "";

    public List<BundleLayerInfo> Bundles { get; init; } = [];

    public Dictionary<string, string> Dependencies { get; init; } = new(StringComparer.Ordinal);

    public List<PluginRowItem> Rows { get; init; } = [];

    public List<InstalledPackageItem> Packages { get; init; } = [];

    /// <summary>dump-config 为最终事实；失败时回退到离线组合。</summary>
    public bool UsedDumpConfig { get; init; }

    public List<string> Warnings { get; init; } = [];

    public string ProfilePatchPath => DshPaths.ProfilePatchPath(ProfileName);

    public string HomePatchPath => DshPaths.HomePatchPath;

    public PluginRowItem? FindRow(string id) => Rows.FirstOrDefault(r => r.Id == id);

    public InstalledPackageItem? FindPackage(string name) =>
        Packages.FirstOrDefault(p => p.Name == name);
}

/// <summary>离线组合时的行状态。</summary>
internal sealed class ComposeRowState
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string? DisabledRaw { get; set; }

    public bool? Disabled { get; set; }

    public string OriginDisabledRaw { get; set; } = "";

    public bool? OriginDisabled { get; set; }

    public string OriginLayer { get; set; } = "";

    public string OriginFile { get; set; } = "";

    public PluginRowOrigin Origin { get; set; }
}

public static partial class PluginInventory
{
    /// <summary>发现 DSH_HOME/profiles 下所有带 package.json 的 profile。</summary>
    public static List<PluginProfileOption> DiscoverProfiles()
    {
        var profilesDir = DshPaths.ProfilesDirectory;
        if (!Directory.Exists(profilesDir))
            return [];

        return Directory.EnumerateDirectories(profilesDir)
            .Where(dir => File.Exists(Path.Combine(dir, "package.json")))
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .Select(dir => new PluginProfileOption { Name = Path.GetFileName(dir) })
            .ToList();
    }

    public static async Task<PluginProfileSnapshot> LoadAsync(
        string profileName, IProgress<string>? progress = null)
    {
        var profileDir = DshPaths.ProfileDirectory(profileName);
        var warnings = new List<string>();
        var packageJsonPath = Path.Combine(profileDir, "package.json");
        var packageJson = ReadPackageJson(packageJsonPath, warnings);
        if (packageJson == null)
        {
            throw new InvalidOperationException(
                $"profile {profileName} 的 package.json 不存在或不是有效 JSON：{packageJsonPath}");
        }

        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (packageJson["dependencies"] is JsonObject deps)
        {
            foreach (var (key, value) in deps)
            {
                if (value != null)
                    dependencies[key] = value.GetValue<string>();
            }
        }

        var bundleNames = new List<string>();
        if (packageJson["dsh"]?["profile"]?["bundles"] is JsonArray bundles)
        {
            bundleNames.AddRange(
                bundles.Where(n => n != null).Select(n => n!.GetValue<string>()).Where(n => n.Length > 0));
        }

        var layers = new List<BundleLayerInfo>();
        foreach (var bundleName in bundleNames)
        {
            var isBuiltIn = !dependencies.ContainsKey(bundleName);
            var dir = ResolveBundleDirectory(bundleName, profileDir);
            if (dir == null)
            {
                warnings.Add($"无法解析 bundle {bundleName}（安装根与 profile node_modules 均未找到）");
                continue;
            }

            var bundlePackageJson = Path.Combine(dir, "package.json");
            if (!File.Exists(bundlePackageJson))
            {
                warnings.Add($"bundle {bundleName} 缺少 package.json：{bundlePackageJson}");
                continue;
            }

            var declaredPatch = "";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(bundlePackageJson));
                if (doc.RootElement.TryGetProperty("dsh", out var dsh)
                    && dsh.TryGetProperty("bundle", out var bundle)
                    && bundle.TryGetProperty("patch", out var patch))
                {
                    declaredPatch = patch.GetString() ?? "";
                }
            }
            catch
            {
                warnings.Add($"bundle {bundleName} 的 package.json 无法解析：{bundlePackageJson}");
                continue;
            }

            var patchPath = string.IsNullOrEmpty(declaredPatch)
                ? Path.Combine(dir, "cordis.patch.yml")
                : Path.GetFullPath(Path.Combine(dir, declaredPatch));
            layers.Add(new BundleLayerInfo
            {
                Name = bundleName,
                PackageDirectory = dir,
                PatchPath = patchPath,
                IsBuiltIn = isBuiltIn,
            });
        }

        var offlineRows = ComposeOffline(profileName, layers, warnings);

        // 已安装时 dump-config 是最终事实。
        var usedDump = false;
        var dumpRows = new List<DumpRow>();
        if (DshCliService.IsInstalled)
        {
            var dump = await DshCliService.DumpConfigAsync(profileName);
            if (dump.ExitCode == 0 && !dump.TimedOut)
            {
                dumpRows = ParseDumpRows(dump.Output);
                if (dumpRows.Count > 0)
                {
                    usedDump = true;
                    progress?.Report($"dsh --profile {profileName} --dump-config：解析到 {dumpRows.Count} 行");
                }
                else
                {
                    warnings.Add("dump-config 成功但没有解析到插件行，改用离线组合");
                }
            }
            else
            {
                warnings.Add("dump-config 不可用（" + dump.Output.Trim().Split('\n')[0] + "），改用离线组合");
            }
        }

        var rows = usedDump
            ? BuildRowsFromDump(profileName, dumpRows, layers, dependencies)
            : offlineRows;

        // 包列表来自 dependencies（包括 bundle-less 普通依赖）。
        var packages = dependencies.Select(kv =>
        {
            var bundleListed = bundleNames.Contains(kv.Key);
            var entity = DshPaths.ResolveModuleDirectory(kv.Key, profileDir);
            return new InstalledPackageItem
            {
                Name = kv.Key,
                Spec = kv.Value,
                IsBundleListed = bundleListed,
                EntityPath = entity ?? "",
            };
        }).ToList();

        foreach (var package in packages)
        {
            package.HasPatchRows = rows.Any(r =>
                string.Equals(r.PackageName, package.Name, StringComparison.Ordinal)
                || PackageOf(r.Name) == package.Name
                || string.Equals(r.OriginLabel, package.Name, StringComparison.Ordinal));
        }

        return new PluginProfileSnapshot
        {
            ProfileName = profileName,
            ProfileDirectory = profileDir,
            Bundles = layers,
            Dependencies = dependencies,
            Rows = rows,
            Packages = packages,
            UsedDumpConfig = usedDump,
            Warnings = warnings,
        };
    }

    private static JsonObject? ReadPackageJson(string path, List<string> warnings)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex)
        {
            warnings.Add($"package.json 无法解析：{ex.Message}");
            return null;
        }
    }

    private static string? ResolveBundleDirectory(string packageName, string profileDirectory)
    {
        // 官方解析顺序：dsh 安装目录优先，然后 profile 自身 node_modules。
        var installRoot = DshPaths.FindDshInstallRoot();
        if (installRoot != null)
        {
            var candidate = Path.Combine(installRoot, "node_modules", packageName);
            if (Directory.Exists(candidate))
                return candidate;
        }

        var fromProfile = DshPaths.ResolveModuleDirectory(packageName, profileDirectory);
        if (fromProfile != null)
            return fromProfile;

        // 内置 bundle 可能从全局 npm 前缀解析。
        var command = DshPaths.FindDshCommand();
        if (command != null)
        {
            var prefix = Path.GetDirectoryName(command);
            if (prefix != null)
            {
                var candidate = Path.Combine(prefix, "node_modules", packageName);
                if (Directory.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static List<PluginRowItem> ComposeOffline(
        string profileName, List<BundleLayerInfo> layers, List<string> warnings)
    {
        var states = new Dictionary<string, ComposeRowState>(StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            ApplyLayer(
                states,
                layer.PatchPath,
                layer.Name,
                layer.PatchPath,
                layer.IsBuiltIn ? PluginRowOrigin.BuiltInBundle : PluginRowOrigin.ProfileBundle,
                warnings);
        }

        var profilePatch = DshPaths.ProfilePatchPath(profileName);
        if (File.Exists(profilePatch))
        {
            ApplyLayer(states, profilePatch, profilePatch, profilePatch, PluginRowOrigin.ProfilePatchInsert, warnings);
        }

        var homePatch = DshPaths.HomePatchPath;
        if (File.Exists(homePatch))
        {
            ApplyLayer(states, homePatch, homePatch, homePatch, PluginRowOrigin.HomePatchInsert, warnings);
        }

        return FinalizeRows(states, layers, new Dictionary<string, string>(StringComparer.Ordinal), profileName);
    }

    private static void ApplyLayer(
        Dictionary<string, ComposeRowState> states,
        string patchPath,
        string layerLabel,
        string sourceFile,
        PluginRowOrigin origin,
        List<string> warnings)
    {
        PatchDocument doc;
        try
        {
            doc = File.Exists(patchPath) ? PatchDocument.Load(patchPath) : PatchDocument.CreateEmpty(patchPath);
        }
        catch (Exception ex)
        {
            warnings.Add($"无法解析 patch {patchPath}：{ex.Message}");
            return;
        }

        if (!doc.ValidateStructure(out var error))
        {
            warnings.Add($"patch {patchPath} 结构异常：{error}");
        }

        foreach (var entry in doc.TopLevelEntries)
        {
            if (entry.IsInsertList)
            {
                foreach (var row in entry.InsertedRows)
                {
                    if (string.IsNullOrWhiteSpace(row.Id))
                        continue;
                    if (states.TryGetValue(row.Id!, out var existing))
                    {
                        // 同 id 手工行视为覆盖原行（与 include 的 buildMap 行为一致）。
                        existing.Name = row.Name ?? existing.Name;
                        ApplyDisabled(existing, row.DisabledRaw, row.Disabled);
                        existing.OriginLayer = layerLabel;
                        existing.OriginFile = sourceFile;
                        existing.Origin = origin;
                        existing.OriginDisabledRaw = row.DisabledRaw ?? "";
                        existing.OriginDisabled = row.Disabled;
                    }
                    else
                    {
                        var state = new ComposeRowState
                        {
                            Id = row.Id!,
                            Name = row.Name ?? "",
                            OriginLayer = layerLabel,
                            OriginFile = sourceFile,
                            Origin = origin,
                            OriginDisabledRaw = row.DisabledRaw ?? "",
                            OriginDisabled = row.Disabled,
                        };
                        ApplyDisabled(state, row.DisabledRaw, row.Disabled);
                        states[row.Id!] = state;
                    }
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Id))
                continue;
            if (!states.TryGetValue(entry.Id!, out var target))
                continue; // 与 include 一致：未命中只警告，不报错。
            if (entry.Name != null && entry.Name != target.Name)
                continue;
            if (entry.Name != null)
                target.Name = entry.Name;
            ApplyDisabled(target, entry.DisabledRaw, entry.Disabled);
        }
    }

    private static void ApplyDisabled(ComposeRowState state, string? raw, bool? value)
    {
        state.DisabledRaw = raw;
        state.Disabled = value;
    }

    private static List<PluginRowItem> FinalizeRows(
        Dictionary<string, ComposeRowState> states,
        List<BundleLayerInfo> layers,
        Dictionary<string, string> dependencies,
        string profileName)
    {
        var rows = new List<PluginRowItem>();
        foreach (var state in states.Values.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            rows.Add(CreateRowItem(state, layers, dependencies, profileName));
        }

        return rows;
    }

    private static PluginRowItem CreateRowItem(
        ComposeRowState state,
        List<BundleLayerInfo> layers,
        Dictionary<string, string> dependencies,
        string profileName)
    {
        var (packageName, spec) = AssociatePackage(state, layers, dependencies);
        var entity = DshPaths.ResolveModuleDirectory(state.Name, DshPaths.ProfileDirectory(profileName));
        var entityInfo = DescribeDirectory(entity);
        var status = string.IsNullOrWhiteSpace(state.DisabledRaw)
            ? PluginRowStatus.Enabled
            : state.Disabled == true ? PluginRowStatus.Disabled
                : state.Disabled == false ? PluginRowStatus.Enabled
                    : PluginRowStatus.Expression;

        return new PluginRowItem
        {
            Id = state.Id,
            Name = state.Name,
            Origin = state.Origin,
            OriginLabel = state.OriginLayer,
            SourceFile = state.OriginFile,
            EntityPath = entity ?? "",
            EntityExists = entity != null,
            EntityIsJunction = entityInfo.IsJunction,
            Status = status,
            DisabledRaw = state.DisabledRaw ?? "",
            OriginDisabledRaw = state.OriginDisabledRaw,
            OriginDisabled = state.OriginDisabled,
            PackageName = packageName,
            DependencySpec = spec,
            UninstallKind = ClassifyUninstall(state, packageName, spec, layers),
        };
    }

    private static (string Package, string Spec) AssociatePackage(
        ComposeRowState state,
        List<BundleLayerInfo> layers,
        Dictionary<string, string> dependencies)
    {
        if (dependencies.TryGetValue(state.Name, out var exact))
            return (state.Name, exact);

        var root = PackageOf(state.Name);
        if (dependencies.TryGetValue(root, out var byRoot))
            return (root, byRoot);

        var layer = layers.FirstOrDefault(l => l.Name == state.OriginLayer);
        if (layer != null && dependencies.TryGetValue(layer.Name, out var byLayer))
            return (layer.Name, byLayer);

        return ("", "");
    }

    public static string PackageOf(string specifier)
    {
        var parts = specifier.Split('/');
        return specifier.StartsWith('@') && parts.Length >= 2
            ? parts[0] + "/" + parts[1]
            : parts[0];
    }

    private static PluginUninstallKind ClassifyUninstall(
        ComposeRowState state, string packageName, string spec, List<BundleLayerInfo> layers)
    {
        if (!string.IsNullOrEmpty(packageName) && !string.IsNullOrEmpty(spec))
        {
            if (spec.StartsWith("file:", StringComparison.Ordinal)
                || spec.StartsWith("link:", StringComparison.Ordinal))
            {
                return PluginUninstallKind.FileOrLinkDependency;
            }

            return state.Origin is PluginRowOrigin.ProfileBundle
                ? PluginUninstallKind.DependencyWithPatchRows
                : PluginUninstallKind.DependencyWithoutPatchRows;
        }

        if (state.Origin is PluginRowOrigin.BuiltInBundle)
            return PluginUninstallKind.BuiltIn;

        if (state.Origin is PluginRowOrigin.ProfileBundle)
        {
            var layer = layers.FirstOrDefault(l => l.Name == state.OriginLayer);
            return layer is { IsBuiltIn: false }
                ? PluginUninstallKind.BundleListedOnly
                : PluginUninstallKind.BuiltIn;
        }

        return PluginUninstallKind.ManualPatchOnly;
    }

    private sealed record DumpRow(
        string Id,
        string Name,
        string DisabledRaw,
        bool? Disabled,
        string OriginLabel,
        string PatchedBy);

    private static List<DumpRow> ParseDumpRows(string output)
    {
        var rows = new List<DumpRow>();
        var currentOrigin = "未知来源";
        var currentPatchedBy = "";
        var i = 0;
        var lines = output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.StartsWith("# ==", StringComparison.Ordinal))
            {
                var label = line[4..].Trim();
                var comma = label.IndexOf(", patched by ", StringComparison.Ordinal);
                if (comma >= 0)
                {
                    currentOrigin = label[..comma].Trim();
                    currentPatchedBy = label[(comma + ", patched by ".Length)..].Trim();
                }
                else
                {
                    currentOrigin = label;
                    currentPatchedBy = "";
                }

                i++;
                continue;
            }

            if (Regex.IsMatch(line, @"^-(\s|$)"))
            {
                var parsed = ParseDumpRowBlock(lines, i, currentOrigin, currentPatchedBy);
                if (parsed != null)
                {
                    rows.Add(parsed.Row);
                    i = parsed.EndLine;
                }
                else
                {
                    i++;
                }

                continue;
            }

            i++;
        }

        return rows;
    }

    private sealed record ParsedDumpRow(DumpRow Row, int EndLine);

    private static ParsedDumpRow? ParseDumpRowBlock(
        string[] lines, int start, string origin, string patchedBy)
    {
        var first = lines[start];
        var id = "";
        var name = "";
        var disabledRaw = "";
        var i = start + 1;

        // 同行的 '- id: x' 或 '- id: x, name: ...' 先解析。
        var inline = Regex.Match(first, @"^-(\s+)?(?<fields>.*)$").Groups["fields"].Value;
        foreach (Match match in Regex.Matches(inline, @"(?<key>id|name|disabled)\s*:\s*(?<value>[^,]*)"))
        {
            AssignDumpField(match.Groups["key"].Value, match.Groups["value"].Value, ref id, ref name, ref disabledRaw);
        }

        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Regex.IsMatch(line, @"^-(\s|$)"))
                break;
            if (line.StartsWith("# ==", StringComparison.Ordinal))
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var match = Regex.Match(line, @"^(?<indent>  )(?<key>id|name|disabled)\s*:\s*(?<value>.*)$");
            if (!match.Success)
                continue;
            AssignDumpField(match.Groups["key"].Value, match.Groups["value"].Value, ref id, ref name, ref disabledRaw);
        }

        if (id.Length == 0)
            return null;

        var (disabled, raw) = PatchDocument.ParseDisabled(disabledRaw);
        return new ParsedDumpRow(
            new DumpRow(id, name, raw, disabled, origin, patchedBy),
            i);
    }

    private static void AssignDumpField(
        string key, string value, ref string id, ref string name, ref string disabled)
    {
        switch (key)
        {
            case "id":
                id = PatchDocument.ParseScalar(value) ?? "";
                break;
            case "name":
                name = PatchDocument.ParseScalar(value) ?? "";
                break;
            case "disabled":
                disabled = value.Trim();
                break;
        }
    }

    private static List<PluginRowItem> BuildRowsFromDump(
        string profileName,
        List<DumpRow> dumpRows,
        List<BundleLayerInfo> layers,
        Dictionary<string, string> dependencies)
    {
        var profilePatchPath = DshPaths.ProfilePatchPath(profileName);
        var homePatchPath = DshPaths.HomePatchPath;
        var builtInNames = layers.Where(l => l.IsBuiltIn).Select(l => l.Name).ToHashSet(StringComparer.Ordinal);

        var rows = new List<PluginRowItem>();
        foreach (var dump in dumpRows)
        {
            PluginRowOrigin origin;
            if (PathEquals(dump.OriginLabel, profilePatchPath))
            {
                origin = PluginRowOrigin.ProfilePatchInsert;
            }
            else if (PathEquals(dump.OriginLabel, homePatchPath))
            {
                origin = PluginRowOrigin.HomePatchInsert;
            }
            else
            {
                origin = builtInNames.Contains(dump.OriginLabel)
                    ? PluginRowOrigin.BuiltInBundle
                    : PluginRowOrigin.ProfileBundle;
            }

            var state = new ComposeRowState
            {
                Id = dump.Id,
                Name = dump.Name,
                DisabledRaw = dump.DisabledRaw,
                Disabled = dump.Disabled,
                OriginLayer = dump.OriginLabel,
                OriginFile = origin is PluginRowOrigin.ProfilePatchInsert
                    ? profilePatchPath
                    : origin is PluginRowOrigin.HomePatchInsert
                        ? homePatchPath
                        : layers.FirstOrDefault(l => l.Name == dump.OriginLabel)?.PatchPath ?? dump.OriginLabel,
                Origin = origin,
            };
            var originDisabled = origin is PluginRowOrigin.ProfilePatchInsert or PluginRowOrigin.HomePatchInsert
                ? (Value: (bool?)null, Raw: "")
                : ReadOriginDisabled(state.OriginFile, dump.Id);
            state.OriginDisabledRaw = originDisabled.Raw;
            state.OriginDisabled = originDisabled.Value;

            rows.Add(CreateRowItem(state, layers, dependencies, profileName));
        }

        return rows;
    }

    private static (bool? Value, string Raw) ReadOriginDisabled(string sourceFile, string id)
    {
        if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile))
            return (null, "");
        try
        {
            var doc = PatchDocument.Load(sourceFile);
            var entry = doc.FindTopLevelOverride(id) ?? doc.FindInsertedRow(id);
            return entry == null
                ? (null, "")
                : (entry.Disabled, entry.DisabledRaw);
        }
        catch
        {
            return (null, "");
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left.Trim('"')),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    public static (bool Exists, bool IsJunction) DescribeDirectory(string? path)
    {
        if (path == null)
            return (false, false);
        try
        {
            var attributes = File.GetAttributes(path);
            return (true, (attributes & FileAttributes.ReparsePoint) != 0);
        }
        catch
        {
            return (false, false);
        }
    }
}
