using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshGUI.Models;

namespace DshGUI.Services;

/// <summary>
/// 插件包（.dshpkg）导出/导入：只携带 Profile 入口配置与本地插件源码，
/// 不复制 node_modules，也不携带 pnpm store 链接。远程依赖保留原 spec，
/// 导入时由官方 dsh plugin install 按 lockfile 重建。
/// </summary>
public sealed class PluginPackageService
{
    private const string ProfileFolder = "profile";
    private const string LocalPluginsFolder = "local-plugins";
    private const string ManifestFile = "manifest.json";

    private static readonly string BackupRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DshGUI", "profile-import");

    private readonly PluginManagerService _pluginManager;

    public PluginPackageService(PluginManagerService pluginManager)
    {
        _pluginManager = pluginManager;
    }

    public async Task<PluginOperationResult> ExportAsync(
        string profileName, string destinationPath, IProgress<string> progress)
    {
        try
        {
            var snapshot = await PluginInventory.LoadAsync(profileName, progress);
            var profileDir = snapshot.ProfileDirectory;

            var temp = CreateTempDir("dshpkg-export");
            try
            {
                var profileOut = Path.Combine(temp, ProfileFolder);
                var localOut = Path.Combine(temp, LocalPluginsFolder);
                Directory.CreateDirectory(profileOut);
                Directory.CreateDirectory(localOut);

                CopyEntryFile(profileDir, "package.json", profileOut);
                CopyEntryFile(profileDir, "pnpm-lock.yaml", profileOut);
                CopyEntryFile(profileDir, "pnpm-workspace.yaml", profileOut);
                CopyEntryFile(profileDir, "cordis.patch.yml", profileOut);
                CopyHomePatchIfExists(temp);

                var manifest = new PluginPackageManifest
                {
                    Dependencies = new Dictionary<string, string>(snapshot.Dependencies, StringComparer.Ordinal),
                    Bundles = snapshot.Bundles.Select(b => b.Name).ToList(),
                };

                foreach (var (packageName, spec) in snapshot.Dependencies)
                {
                    if (!TryGetLocalDependencyPath(spec, profileDir, out var localPath)
                        || !Directory.Exists(localPath))
                    {
                        manifest.RemoteDependencies.Add(packageName);
                        continue;
                    }

                    var key = SanitizeKey(packageName);
                    DshFileSystem.CopyDirectory(localPath, Path.Combine(localOut, key), progress);
                    manifest.LocalPlugins.Add(new LocalPluginEntry
                    {
                        Key = key,
                        PackageName = packageName,
                    });
                    progress.Report($"已打包本地依赖：{packageName}（{localPath}）");
                }

                // 手工 patch 行：不在 dependencies 里，但实体在 profile node_modules 下。
                foreach (var row in snapshot.Rows.Where(r =>
                             r.Origin is PluginRowOrigin.ProfilePatchInsert or PluginRowOrigin.HomePatchInsert
                             && string.IsNullOrEmpty(r.PackageName)))
                {
                    if (string.IsNullOrEmpty(row.EntityPath)
                        || !row.EntityExists
                        || !DshFileSystem.IsPathUnder(
                            Path.Combine(profileDir, "node_modules"), row.EntityPath))
                    {
                        continue;
                    }

                    var packageName = PluginInventory.PackageOf(row.Name);
                    if (manifest.LocalPlugins.Any(p => p.PackageName == packageName))
                        continue;

                    var key = SanitizeKey(packageName);
                    DshFileSystem.CopyDirectory(row.EntityPath, Path.Combine(localOut, key), progress);
                    manifest.LocalPlugins.Add(new LocalPluginEntry
                    {
                        Key = key,
                        PackageName = packageName,
                    });
                    progress.Report($"已打包手工插件：{packageName}（{row.EntityPath}）");
                }

                await File.WriteAllTextAsync(
                    Path.Combine(temp, ManifestFile),
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

                if (File.Exists(destinationPath))
                    File.Delete(destinationPath);
                ZipFile.CreateFromDirectory(temp, destinationPath, CompressionLevel.Optimal, includeBaseDirectory: false);

                var summary = string.Join("、", manifest.LocalPlugins.Select(p => p.PackageName).DefaultIfEmpty("无"));
                return PluginOperationResult.Ok(
                    $"已导出 {destinationPath}\n本地插件：{summary}；远程依赖：{string.Join("、", manifest.RemoteDependencies.DefaultIfEmpty("无"))}");
            }
            finally
            {
                TryDeleteDirectory(temp);
            }
        }
        catch (Exception ex)
        {
            return PluginOperationResult.Fail("导出插件包失败：" + ex.Message);
        }
    }

    /// <summary>打开 .dshpkg 后、实际导入前做格式自检，不修改任何 profile 文件。</summary>
    public PluginPackageValidation ValidatePackage(string packagePath)
    {
        var result = new PluginPackageValidation();
        var temp = CreateTempDir("dshpkg-validate");
        try
        {
            try
            {
                ZipFile.ExtractToDirectory(packagePath, temp);
            }
            catch (Exception ex)
            {
                result.Errors.Add("无法打开插件包：" + ex.Message);
                return result;
            }

            var manifestPath = Path.Combine(temp, ManifestFile);
            if (!File.Exists(manifestPath))
            {
                result.Errors.Add("缺少 manifest.json");
                return result;
            }

            PluginPackageManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<PluginPackageManifest>(File.ReadAllText(manifestPath));
            }
            catch (Exception ex)
            {
                result.Errors.Add("manifest.json 无法解析：" + ex.Message);
                return result;
            }

            if (manifest == null)
            {
                result.Errors.Add("manifest.json 内容为空");
                return result;
            }

            if (manifest.FormatVersion != 1)
                result.Errors.Add($"不支持的插件包版本：{manifest.FormatVersion}");

            var packageJsonPath = Path.Combine(temp, ProfileFolder, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                result.Errors.Add("缺少 profile/package.json");
            }
            else
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(packageJsonPath)) is not JsonObject)
                        result.Errors.Add("profile/package.json 顶层不是 JSON 对象");
                }
                catch (Exception ex)
                {
                    result.Errors.Add("profile/package.json 无法解析：" + ex.Message);
                }
            }

            foreach (var local in manifest.LocalPlugins)
            {
                if (string.IsNullOrWhiteSpace(local.Key) || string.IsNullOrWhiteSpace(local.PackageName))
                {
                    result.Errors.Add("本地插件条目缺少 Key 或 PackageName");
                    continue;
                }

                if (!Directory.Exists(Path.Combine(temp, LocalPluginsFolder, local.Key)))
                    result.Errors.Add($"本地插件目录缺失：{local.Key}");
            }

            foreach (var remote in manifest.RemoteDependencies)
            {
                if (!manifest.Dependencies.TryGetValue(remote, out var spec) || string.IsNullOrWhiteSpace(spec))
                    result.Errors.Add($"远程依赖缺少 spec：{remote}");
            }

            var patchPath = Path.Combine(temp, ProfileFolder, "cordis.patch.yml");
            if (File.Exists(patchPath))
            {
                try
                {
                    var doc = PatchDocument.Load(patchPath);
                    if (!doc.ValidateStructure(out var error))
                        result.Errors.Add($"profile/cordis.patch.yml 结构异常：{error}");
                }
                catch (Exception ex)
                {
                    result.Errors.Add("profile/cordis.patch.yml 无法解析：" + ex.Message);
                }
            }

            if (!File.Exists(Path.Combine(temp, ProfileFolder, "pnpm-lock.yaml")))
                result.Warnings.Add("缺少 pnpm-lock.yaml，导入后将重新解析依赖");
            if (!File.Exists(Path.Combine(temp, ProfileFolder, "pnpm-workspace.yaml")))
                result.Warnings.Add("缺少 pnpm-workspace.yaml，将使用当前 profile 设置");
        }
        finally
        {
            TryDeleteDirectory(temp);
        }

        return result;
    }

    public async Task<PluginImportPreview?> PreviewImportAsync(string profileName, string packagePath)
    {
        if (!ValidatePackage(packagePath).Valid)
            return null;

        var temp = CreateTempDir("dshpkg-preview");
        try
        {
            ZipFile.ExtractToDirectory(packagePath, temp);
            var manifestPath = Path.Combine(temp, ManifestFile);
            if (!File.Exists(manifestPath))
                return null;
            var manifest = JsonSerializer.Deserialize<PluginPackageManifest>(
                await File.ReadAllTextAsync(manifestPath));
            if (manifest == null || manifest.FormatVersion != 1)
                return null;

            var snapshot = await PluginInventory.LoadAsync(profileName);
            var currentDeps = snapshot.Dependencies.Keys.ToHashSet(StringComparer.Ordinal);
            var currentRows = snapshot.Rows.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
            var preview = new PluginImportPreview
            {
                ProfileName = profileName,
            };

            foreach (var local in manifest.LocalPlugins)
                AddToPreview(preview, local.PackageName, currentDeps, currentRows);
            foreach (var remote in manifest.RemoteDependencies)
                AddToPreview(preview, remote, currentDeps, currentRows);

            var packagePatch = Path.Combine(temp, ProfileFolder, "cordis.patch.yml");
            if (File.Exists(packagePatch))
            {
                var doc = PatchDocument.Load(packagePatch);
                foreach (var row in doc.TopLevelEntries.SelectMany(e => e.InsertedRows))
                {
                    if (row.Name == null || IsInBoxBundle(row.Name))
                        continue;
                    if (currentRows.Contains(row.Name) || currentDeps.Contains(PluginInventory.PackageOf(row.Name)))
                    {
                        if (!preview.Duplicates.Contains(row.Name))
                            preview.Duplicates.Add(row.Name);
                    }
                    else if (!preview.Additions.Contains(row.Name))
                    {
                        preview.Additions.Add(row.Name);
                    }
                }
            }

            preview.Duplicates = preview.Duplicates
                .Where(n => !IsInBoxBundle(n))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            preview.Additions = preview.Additions
                .Where(n => !IsInBoxBundle(n))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            preview.Items = preview.Duplicates
                .Select(name => new PluginImportPreviewItem
                {
                    Name = name,
                    IsDuplicate = true,
                    IsSelected = false,
                })
                .Concat(preview.Additions.Select(name => new PluginImportPreviewItem
                {
                    Name = name,
                    IsDuplicate = false,
                    IsSelected = true,
                }))
                .ToList();
            return preview;
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    private static void AddToPreview(
        PluginImportPreview preview, string packageName, HashSet<string> currentDeps, HashSet<string> currentRows)
    {
        if (string.IsNullOrWhiteSpace(packageName) || IsInBoxBundle(packageName))
            return;

        var exists = currentDeps.Contains(packageName) || currentRows.Contains(packageName);
        var target = exists ? preview.Duplicates : preview.Additions;
        target.Add(packageName);
    }

    private static bool IsInBoxBundle(string name) =>
        name.StartsWith("@deepseek-ai/dsh-base", StringComparison.Ordinal)
        || name.StartsWith("@deepseek-ai/dsh-web-app", StringComparison.Ordinal)
        || name.StartsWith("@deepseek-ai/dsh-headless", StringComparison.Ordinal);

    public async Task<PluginOperationResult> ImportAsync(
        string profileName,
        string packagePath,
        IReadOnlyCollection<string> selectedNames,
        IProgress<string> progress)
    {
        var backupDir = Path.Combine(BackupRoot, Guid.NewGuid().ToString("N"));
        var temp = CreateTempDir("dshpkg-import");
        try
        {
            ZipFile.ExtractToDirectory(packagePath, temp);
            var manifestPath = Path.Combine(temp, ManifestFile);
            if (!File.Exists(manifestPath))
                return PluginOperationResult.Fail("插件包缺少 manifest.json");
            var manifest = JsonSerializer.Deserialize<PluginPackageManifest>(
                await File.ReadAllTextAsync(manifestPath))
                ?? throw new InvalidOperationException("manifest.json 为空");
            if (manifest.FormatVersion != 1)
                return PluginOperationResult.Fail($"不支持的插件包版本：{manifest.FormatVersion}");

            var selected = selectedNames.ToHashSet(StringComparer.Ordinal);

            var profileDir = DshPaths.ProfileDirectory(profileName);
            Directory.CreateDirectory(profileDir);

            var stop = await _pluginManager.StopDshForOperationAsync(progress);
            if (!stop.Success)
                return PluginOperationResult.Fail(stop.Message);

            Directory.CreateDirectory(backupDir);
            if (Directory.Exists(profileDir))
                DshFileSystem.CopyDirectory(profileDir, Path.Combine(backupDir, "profile"), progress);
            var homePatch = DshPaths.HomePatchPath;
            var hadHomePatch = File.Exists(homePatch);
            if (hadHomePatch)
                File.Copy(homePatch, Path.Combine(backupDir, "home-patch.yml"), overwrite: true);

            try
            {
                var profileZip = Path.Combine(temp, ProfileFolder);
                var localZip = Path.Combine(temp, LocalPluginsFolder);
                var packageJsonPath = Path.Combine(profileDir, "package.json");

                // 1. 以当前 profile 为底，只补包里有、当前没有的插件；已有的一律不替换。
                JsonObject root;
                if (File.Exists(packageJsonPath))
                {
                    root = JsonNode.Parse(await File.ReadAllTextAsync(packageJsonPath)) as JsonObject
                        ?? throw new InvalidOperationException("当前 profile package.json 不是对象");
                }
                else
                {
                    root = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(profileZip, "package.json"))) as JsonObject
                        ?? throw new InvalidOperationException("插件包 package.json 不是对象");
                }

                var deps = root["dependencies"] as JsonObject ?? [];
                root["dependencies"] = deps;

                var localTargetRoot = Path.Combine(profileDir, "dshgui-local");
                Directory.CreateDirectory(localTargetRoot);
                var addedPlugins = new List<string>();
                var skippedPlugins = new List<string>();
                foreach (var local in manifest.LocalPlugins)
                {
                    if (!selected.Contains(local.PackageName))
                        continue;
                    if (deps.ContainsKey(local.PackageName))
                    {
                        skippedPlugins.Add(local.PackageName + "（已存在，不替换）");
                        continue;
                    }

                    var source = Path.Combine(localZip, local.Key);
                    var target = Path.Combine(localTargetRoot, local.Key);
                    if (!Directory.Exists(target) && Directory.Exists(source))
                    {
                        DshFileSystem.CopyDirectory(source, target, progress);
                        progress.Report($"已恢复本地插件：{local.PackageName} → {target}");
                    }
                    else
                    {
                        progress.Report($"本地插件目录已存在，复用：{target}");
                    }

                    deps[local.PackageName] = $"file:./dshgui-local/{local.Key}";
                    addedPlugins.Add(local.PackageName);
                }

                foreach (var remote in manifest.RemoteDependencies)
                {
                    if (!selected.Contains(remote))
                        continue;
                    if (deps.ContainsKey(remote))
                    {
                        skippedPlugins.Add(remote + "（已存在，不替换）");
                        continue;
                    }

                    if (manifest.Dependencies.TryGetValue(remote, out var spec) && !string.IsNullOrWhiteSpace(spec))
                    {
                        deps[remote] = spec;
                        addedPlugins.Add(remote);
                    }
                }

                // bundles 同样只补缺失项。
                var dsh = root["dsh"] as JsonObject ?? [];
                root["dsh"] = dsh;
                var profile = dsh["profile"] as JsonObject ?? [];
                dsh["profile"] = profile;
                var bundles = profile["bundles"] as JsonArray ?? [];
                profile["bundles"] = bundles;
                foreach (var bundle in manifest.Bundles)
                {
                    if (!selected.Contains(bundle))
                        continue;
                    if (!bundles.Any(n => n != null && n.GetValue<string>() == bundle))
                        bundles.Add(bundle);
                }

                DshFileSystem.WriteAllTextAtomic(
                    packageJsonPath,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

                // 2. lockfile / workspace 只补缺失文件，不覆盖当前已有内容。
                foreach (var entry in new[] { "pnpm-lock.yaml", "pnpm-workspace.yaml" })
                {
                    var target = Path.Combine(profileDir, entry);
                    var source = Path.Combine(profileZip, entry);
                    if (!File.Exists(target) && File.Exists(source))
                        File.Copy(source, target, overwrite: true);
                }

                // 3. patch 按 id 合并：包里的行只补当前缺失的 id。
                var packagePatchSource = Path.Combine(profileZip, "cordis.patch.yml");
                if (File.Exists(packagePatchSource))
                {
                    var sourceDoc = PatchDocument.Load(packagePatchSource);
                    var currentPatchPath = DshPaths.ProfilePatchPath(profileName);
                    var currentDoc = File.Exists(currentPatchPath)
                        ? PatchDocument.Load(currentPatchPath)
                        : PatchDocument.CreateEmpty(currentPatchPath);
                    currentDoc.MergeMissingFrom(sourceDoc, selected);
                    currentDoc.Save();
                }

                var homePatchZip = Path.Combine(temp, "home-patch.yml");
                if (!hadHomePatch && File.Exists(homePatchZip))
                {
                    DshFileSystem.WriteAllTextAtomic(homePatch, await File.ReadAllTextAsync(homePatchZip));
                }

                var install = await DshCliService.InstallProfileAsync(profileName, progress);
                if (install.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "dsh plugin install 失败（" + install.ExitCode + "）：\n" + TrimOutput(install.Output));
                }

                var validation = await _pluginManager.ValidateProfileAsync(profileName, progress);
                if (!validation.Valid)
                    throw new InvalidOperationException("导入后复查失败：\n" + validation.Message);

                progress.Report("导入完成，请重启 dsh 加载新插件");
                var summary = "已导入插件包到 " + profileDir;
                if (addedPlugins.Count > 0)
                    summary += "\n新增：" + string.Join("、", addedPlugins);
                if (skippedPlugins.Count > 0)
                    summary += "\n跳过（已存在，未替换）：" + string.Join("、", skippedPlugins);
                summary += "\n请点击「重启 dsh」。";
                return PluginOperationResult.Ok(summary);
            }
            catch (Exception ex)
            {
                progress.Report("导入失败，恢复原 profile：" + ex.Message);
                RestoreBackup(backupDir, profileDir, progress);
                return PluginOperationResult.Fail(ex.Message);
            }
        }
        catch (Exception ex)
        {
            return PluginOperationResult.Fail("导入插件包失败：" + ex.Message);
        }
        finally
        {
            TryDeleteDirectory(temp);
            CleanupImportBackups();
        }
    }

    private static void CleanupImportBackups()
    {
        try
        {
            if (!Directory.Exists(BackupRoot))
                return;

            const int keep = 5;
            foreach (var old in Directory.EnumerateDirectories(BackupRoot)
                         .OrderByDescending(Directory.GetLastWriteTime)
                         .Skip(keep))
            {
                TryDeleteDirectory(old);
            }
        }
        catch
        {
            // 清理旧备份失败不影响导入结果。
        }
    }

    private static void CopyEntryFile(string sourceDir, string fileName, string destDir)
    {
        var source = Path.Combine(sourceDir, fileName);
        if (File.Exists(source))
            File.Copy(source, Path.Combine(destDir, fileName), overwrite: true);
    }

    private static void CopyHomePatchIfExists(string temp)
    {
        var homePatch = DshPaths.HomePatchPath;
        if (File.Exists(homePatch))
            File.Copy(homePatch, Path.Combine(temp, "home-patch.yml"), overwrite: true);
    }

    private static bool TryGetLocalDependencyPath(string spec, string profileDir, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(spec))
            return false;

        string candidate;
        if (spec.StartsWith("file:", StringComparison.Ordinal)
            || spec.StartsWith("link:", StringComparison.Ordinal))
        {
            var colon = spec.IndexOf(':');
            candidate = spec[(colon + 1)..];
        }
        else
        {
            candidate = spec;
        }

        if (candidate.StartsWith('.') || Path.IsPathRooted(candidate))
        {
            path = Path.GetFullPath(Path.Combine(profileDir, candidate));
            return true;
        }

        return false;
    }

    private static string SanitizeKey(string packageName)
    {
        var safe = new string(packageName.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "plugin" : safe.Trim('-');
    }

    private static void RestoreBackup(string backupDir, string profileDir, IProgress<string> progress)
    {
        var profileBackup = Path.Combine(backupDir, "profile");
        if (Directory.Exists(profileDir))
            DshFileSystem.DeletePathSafe(profileDir);
        if (Directory.Exists(profileBackup))
            DshFileSystem.CopyDirectory(profileBackup, profileDir, progress);

        var homeBackup = Path.Combine(backupDir, "home-patch.yml");
        if (File.Exists(homeBackup))
            DshFileSystem.WriteAllTextAtomic(
                DshPaths.HomePatchPath, DshFileSystem.ReadAllTextNoBomSafe(homeBackup));
    }

    private static string CreateTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                DshFileSystem.DeletePathSafe(path);
        }
        catch
        {
            // 临时目录清理失败不影响结果。
        }
    }

    private static string TrimOutput(string output)
    {
        var lines = output.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(8);
        return string.Join("\n", lines);
    }
}
