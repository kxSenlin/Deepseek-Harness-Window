using System.Text.Json;
using DshGUI.Models;

namespace DshGUI.Services;

/// <summary>
/// 插件屏蔽/卸载/撤销的业务流程。所有写操作遵循 PLAN 第五～八节：
/// 先停 dsh → 轮询配置端口 → 暂存 → 修改 → 校验 → 失败自动撤销 → 重启 dsh。
/// </summary>
public sealed class PluginManagerService : IDisposable
{
    private const string AppDataDirectoryName = "DshGUI";
    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private readonly DshService _dsh;
    private readonly Func<Task> _restartDsh;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly HashSet<string> _existingRows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _existingPackages = new(StringComparer.Ordinal);
    private int _undoSequence;

    public PluginManagerService(DshService dsh, Func<Task> restartDsh)
    {
        _dsh = dsh;
        _restartDsh = restartDsh;
    }

    private string SessionRoot => Path.Combine(LocalAppData, AppDataDirectoryName, "uninstall-undo", _sessionId);

    private string MutationRoot => Path.Combine(LocalAppData, AppDataDirectoryName, "plugin-ops", _sessionId);

    /// <summary>首次盘点后调用：既有插件卸载时必须双重严格确认。</summary>
    public void MarkInventory(PluginProfileSnapshot snapshot)
    {
        foreach (var row in snapshot.Rows)
            _existingRows.Add(snapshot.ProfileName + "|" + row.Id);
        foreach (var package in snapshot.Packages)
            _existingPackages.Add(snapshot.ProfileName + "|" + package.Name);
    }

    public bool IsExistingRow(string profileName, string rowId) =>
        _existingRows.Contains(profileName + "|" + rowId);

    public bool IsExistingPackage(string profileName, string packageName) =>
        _existingPackages.Contains(profileName + "|" + packageName);

    // ------------------------------------------------------------------
    // 屏蔽 / 解除屏蔽
    // ------------------------------------------------------------------

    public async Task<PluginOperationResult> SetRowDisabledAsync(
        string profileName, string rowId, bool disabled, IProgress<string> log, bool restartAfter = true)
    {
        try
        {
            var snapshot = await PluginInventory.LoadAsync(profileName, log);
            var row = snapshot.FindRow(rowId);
            if (row == null)
                return PluginOperationResult.Fail($"找不到插件行 {rowId}");

            if (row.UninstallKind == PluginUninstallKind.BuiltIn && disabled)
            {
                // 内置核心行警告由 VM 在调用前确认；这里只记录。
                log.Report($"警告：{row.Id} 是内置核心行，屏蔽后 dsh 可能无法启动");
            }

            var stop = await StopDshForOperationAsync(log);
            if (!stop.Success)
                return PluginOperationResult.Fail(stop.Message);

            var preDumpOk = await IsDumpConfigHealthyAsync(profileName, log);

            var target = LocatePatchTarget(snapshot, row);
            if (target == null)
                return PluginOperationResult.Fail("无法定位可编辑的 patch 文件");

            if (!File.Exists(target.Path))
            {
                log.Report($"patch 文件不存在，创建空层：{target.Path}");
                PatchDocument.CreateEmpty(target.Path).Save();
            }

            var backupDir = Path.Combine(MutationRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(backupDir);
            var backupPath = Path.Combine(backupDir, Path.GetFileName(target.Path));
            File.Copy(target.Path, backupPath, overwrite: true);
            log.Report($"已暂存目标 patch：{backupPath}");

            var doc = PatchDocument.Load(target.Path);
            var entry = doc.FindTopLevelOverride(row.Id) ?? doc.FindInsertedRow(row.Id);
            if (!disabled)
            {
                if (entry == null)
                {
                    // bundle 插入的行没有用户覆盖：写 disabled: false 覆盖 bundle 层。
                    doc.AddOrUpdateTopLevelOverride(row.Id, false);
                }
                else if (row.OriginDisabled == true || !string.IsNullOrWhiteSpace(row.OriginDisabledRaw))
                {
                    // 原层自带屏蔽（含表达式）：移除会恢复屏蔽，必须写 false 覆盖。
                    doc.SetDisabled(row.Id, false);
                }
                else
                {
                    // 手工行/普通覆盖：移除 disabled 字段即恢复启用（PLAN 第五节）。
                    doc.SetDisabled(row.Id, null);
                }
            }
            else if (entry == null
                && row.Origin is PluginRowOrigin.BuiltInBundle or PluginRowOrigin.ProfileBundle)
            {
                // bundle 插入的行：在 profile/home patch 按 id 覆盖。
                doc.AddOrUpdateTopLevelOverride(row.Id, true);
            }
            else
            {
                var result = doc.SetDisabled(row.Id, true);
                if (result == null)
                    return PluginOperationResult.Fail($"patch 中没有可编辑的条目 {row.Id}");
            }

            doc.Save();
            log.Report($"已写入 {target.Path}：{row.Id} -> {(disabled ? "屏蔽" : "启用")}");

            var validation = await ValidateProfileAsync(profileName, log);
            if (!validation.Valid)
            {
                if (preDumpOk)
                {
                    RestoreFile(backupPath, target.Path);
                    return PluginOperationResult.Fail("修改后校验失败，已自动恢复原 patch：\n" + validation.Message);
                }

                log.Report("修改前 dump-config 即不可用；保留本次修改，请重启后确认");
            }

            if (restartAfter)
            {
                await RestartManagedDshAsync(log);
                return PluginOperationResult.Ok(
                    $"已{(disabled ? "屏蔽" : "启用")} {row.Id}，dsh 重启请求已发出");
            }

            log.Report("dsh 保持停止状态；请稍后点击「重启 dsh」");
            return PluginOperationResult.Ok(
                $"已{(disabled ? "屏蔽" : "启用")} {row.Id}。dsh 已停止，请稍后点击「重启 dsh」");
        }
        catch (Exception ex)
        {
            return PluginOperationResult.Fail("操作失败：" + ex.Message);
        }
    }

    private sealed record PatchTarget(string Path);

    private PatchTarget? LocatePatchTarget(PluginProfileSnapshot snapshot, PluginRowItem row)
    {
        var profilePatch = snapshot.ProfilePatchPath;
        var homePatch = snapshot.HomePatchPath;
        var homeDoc = File.Exists(homePatch) ? PatchDocument.Load(homePatch) : null;
        var profileDoc = File.Exists(profilePatch) ? PatchDocument.Load(profilePatch) : null;

        // 后层优先：home patch 中已有该 id 的覆盖时改 home，否则 profile。
        if (homeDoc?.FindTopLevelOverride(row.Id) != null || homeDoc?.FindInsertedRow(row.Id) != null)
            return new PatchTarget(homePatch);

        if (profileDoc?.FindTopLevelOverride(row.Id) != null || profileDoc?.FindInsertedRow(row.Id) != null)
            return new PatchTarget(profilePatch);

        return row.Origin switch
        {
            PluginRowOrigin.ProfilePatchInsert => File.Exists(profilePatch) ? new PatchTarget(profilePatch) : null,
            PluginRowOrigin.HomePatchInsert => File.Exists(homePatch) ? new PatchTarget(homePatch) : null,
            _ => new PatchTarget(profilePatch),
        };
    }

    // ------------------------------------------------------------------
    // 卸载
    // ------------------------------------------------------------------

    public async Task<PluginOperationResult> UninstallRowAsync(
        string profileName, string rowId, bool deleteExternalSource, IProgress<string> log)
    {
        var snapshot = await PluginInventory.LoadAsync(profileName, log);
        var row = snapshot.FindRow(rowId);
        if (row == null)
            return PluginOperationResult.Fail($"找不到插件行 {rowId}");

        return await UninstallCoreAsync(snapshot, row, packageName: row.PackageName, deleteExternalSource, log);
    }

    public async Task<PluginOperationResult> UninstallPackageAsync(
        string profileName, string packageName, bool deleteExternalSource, IProgress<string> log)
    {
        var snapshot = await PluginInventory.LoadAsync(profileName, log);
        var package = snapshot.FindPackage(packageName);
        if (package == null)
            return PluginOperationResult.Fail($"dependencies 中没有 {packageName}");

        return await UninstallCoreAsync(snapshot, row: null, package.Name, deleteExternalSource, log);
    }

    private async Task<PluginOperationResult> UninstallCoreAsync(
        PluginProfileSnapshot snapshot,
        PluginRowItem? row,
        string packageName,
        bool deleteExternalSource,
        IProgress<string> log)
    {
        try
        {
            var profileName = snapshot.ProfileName;
            var manualName = row?.Name ?? "";
            var package = snapshot.FindPackage(packageName);
            var kind = row?.UninstallKind
                ?? (package is { IsFileOrLink: true }
                    ? PluginUninstallKind.FileOrLinkDependency
                    : package is { HasPatchRows: true }
                        ? PluginUninstallKind.DependencyWithPatchRows
                        : PluginUninstallKind.DependencyWithoutPatchRows);

            if (kind == PluginUninstallKind.BuiltIn)
                return PluginOperationResult.Fail("内置插件只提供屏蔽，不提供卸载。");

            var stop = await StopDshForOperationAsync(log);
            if (!stop.Success)
                return PluginOperationResult.Fail(stop.Message);

            var relatedRowIds = snapshot.Rows
                .Where(r => References(r, packageName, manualName))
                .Select(r => r.Id)
                .ToHashSet(StringComparer.Ordinal);
            var homeInvolved = row?.Origin == PluginRowOrigin.HomePatchInsert
                || relatedRowIds.Any(id => PatchReferencesId(snapshot.HomePatchPath, id));
            var externalBackups = new List<ExternalBackup>();
            var recordDir = await StageUninstallAsync(snapshot, packageName, manualName, homeInvolved, externalBackups, log);

            try
            {
                switch (kind)
                {
                    case PluginUninstallKind.DependencyWithPatchRows:
                    case PluginUninstallKind.DependencyWithoutPatchRows:
                    case PluginUninstallKind.FileOrLinkDependency:
                    {
                        log.Report($"执行官方卸载：dsh plugin --profile {profileName} remove {packageName}");
                        var remove = await DshCliService.RemovePluginAsync(profileName, packageName, log);
                        if (remove.ExitCode != 0)
                        {
                            throw new InvalidOperationException(
                                "dsh plugin remove 失败（" + remove.ExitCode + "）：\n" + TrimOutput(remove.Output));
                        }

                        CleanupPatchReferences(profileName, packageName, manualName, relatedRowIds, log);

                        if (deleteExternalSource && kind == PluginUninstallKind.FileOrLinkDependency)
                        {
                            if (package is { EntityPath.Length: > 0 }
                                && !DshFileSystem.IsPathUnder(snapshot.ProfileDirectory, package.EntityPath))
                            {
                                await StageAndDeleteExternalAsync(recordDir, package.EntityPath, externalBackups, log);
                            }
                        }

                        break;
                    }

                    case PluginUninstallKind.ManualPatchOnly:
                    {
                        if (row == null)
                            throw new InvalidOperationException("手工插件缺少行信息");
                        CleanupPatchReferences(profileName, "", row.Name, relatedRowIds, log);
                        if (row.EntityPath.Length > 0)
                        {
                            if (!DshFileSystem.IsPathUnder(snapshot.ProfileDirectory, row.EntityPath))
                            {
                                await StageAndDeleteExternalAsync(recordDir, row.EntityPath, externalBackups, log);
                            }
                            else if (row.EntityExists)
                            {
                                log.Report($"删除实体目录：{row.EntityPath}");
                                DshFileSystem.DeletePathSafe(row.EntityPath);
                            }
                        }

                        break;
                    }

                    case PluginUninstallKind.BundleListedOnly:
                    {
                        var editor = ProfileManifestEditor.Load(profileName);
                        editor.RemoveBundle(packageName);
                        editor.Save();
                        log.Report($"已从 dsh.profile.bundles 移除 {packageName}");

                        CleanupPatchReferences(profileName, packageName, manualName, relatedRowIds, log);

                        var bundle = snapshot.Bundles.FirstOrDefault(b => b.Name == packageName);
                        if (bundle is { PackageDirectory.Length: > 0 }
                            && DshFileSystem.IsPathUnder(snapshot.ProfileDirectory, bundle.PackageDirectory))
                        {
                            log.Report($"删除 profile 内 bundle 实体：{bundle.PackageDirectory}");
                            DshFileSystem.DeletePathSafe(bundle.PackageDirectory);
                        }

                        break;
                    }

                    default:
                        throw new InvalidOperationException($"不支持的卸载分类：{kind}");
                }

                var validation = await ValidateUninstallAsync(profileName, packageName, manualName, kind, log);
                if (!validation.Valid)
                    throw new InvalidOperationException("卸载后复查失败，自动撤销本次操作：\n" + validation.Message);

                var hashes = await BuildCurrentHashesAsync(snapshot, homeInvolved, externalBackups);
                var data = await LoadRecordAsync(recordDir);
                data.HashAfter = hashes;
                data.ExternalBackups = externalBackups;
                await SaveRecordAsync(recordDir, data);
                log.Report($"卸载完成；撤销副本：{recordDir}");

                await RestartManagedDshAsync(log);
                return PluginOperationResult.Ok($"已卸载 {packageName}，并可从本会话撤销");
            }
            catch (Exception ex)
            {
                log.Report("操作失败，自动恢复暂存副本…");
                await RestoreBackupAsync(recordDir, overwriteChanged: true, log);
                return PluginOperationResult.Fail(ex.Message);
            }
        }
        catch (Exception ex)
        {
            return PluginOperationResult.Fail("卸载失败：" + ex.Message);
        }
    }

    private static bool References(PluginRowItem row, string packageName, string manualName)
    {
        if (packageName.Length > 0)
        {
            if (row.PackageName == packageName)
                return true;
            if (PluginInventory.PackageOf(row.Name) == packageName)
                return true;
        }

        return manualName.Length > 0 && row.Name == manualName;
    }

    private static bool PatchReferencesId(string patchPath, string id)
    {
        if (!File.Exists(patchPath))
            return false;
        try
        {
            var doc = PatchDocument.Load(patchPath);
            return doc.FindTopLevelOverride(id) != null || doc.FindInsertedRow(id) != null;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupPatchReferences(
        string profileName, string packageName, string manualName, HashSet<string> relatedRowIds, IProgress<string> log)
    {
        foreach (var patchPath in PatchPathsFor(profileName))
        {
            if (!File.Exists(patchPath))
                continue;
            var doc = PatchDocument.Load(patchPath);
            var removedIds = new HashSet<string>(StringComparer.Ordinal);
            var removeNames = new HashSet<string>(StringComparer.Ordinal);
            if (packageName.Length > 0)
                removeNames.Add(packageName);
            if (manualName.Length > 0)
                removeNames.Add(manualName);

            foreach (var entry in doc.TopLevelEntries)
            {
                foreach (var inserted in entry.InsertedRows)
                {
                    if (inserted.Name == null)
                        continue;
                    if (!removeNames.Contains(inserted.Name)
                        && (packageName.Length == 0
                            || PluginInventory.PackageOf(inserted.Name) != packageName))
                    {
                        continue;
                    }

                    removedIds.Add(inserted.Id ?? "");
                }
            }

            removedIds.UnionWith(relatedRowIds);

            if (removedIds.Count > 0)
            {
                foreach (var id in removedIds)
                    doc.RemoveEntry(id, fromInsertedRows: true);
                foreach (var id in removedIds)
                    doc.RemoveEntry(id, fromInsertedRows: false);
                doc.Save();
                log.Report($"已清理 patch 引用：{patchPath}");
            }
        }
    }

    private async Task StageAndDeleteExternalAsync(
        string recordDir, string path, List<ExternalBackup> backups, IProgress<string> log)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            return;
        var name = "external-" + backups.Count.ToString("00");
        backups.Add(new ExternalBackup
        {
            Name = name,
            OriginalPath = path,
            IsJunction = DshFileSystem.IsReparsePoint(path),
            LinkTarget = DshFileSystem.GetLinkTarget(path) ?? "",
        });

        var backupPath = Path.Combine(recordDir, "backup", "external", name);
        Directory.CreateDirectory(backupPath);
        if (Directory.Exists(path))
            DshFileSystem.CopyDirectory(path, backupPath, log);
        else
            File.Copy(path, Path.Combine(backupPath, Path.GetFileName(path)), overwrite: true);

        // 立即持久化：后续任何一步失败，自动回滚都知道这个外部实体已备份。
        var record = await LoadRecordAsync(recordDir);
        record.ExternalBackups = backups;
        await SaveRecordAsync(recordDir, record);

        log.Report($"删除外部实体：{path}");
        DshFileSystem.DeletePathSafe(path);
    }

    // ------------------------------------------------------------------
    // 校验
    // ------------------------------------------------------------------

    private async Task<(bool Valid, string Message)> ValidateProfileAsync(string profileName, IProgress<string> log)
    {
        var errors = new List<string>();
        AddManifestParseError(profileName, errors);
        errors.AddRange(await CollectPatchAndDumpErrorsAsync(profileName));
        return Summarize(errors);
    }

    private async Task<bool> IsDumpConfigHealthyAsync(string profileName, IProgress<string> log)
    {
        if (!DshCliService.IsInstalled)
            return true;
        var dump = await DshCliService.DumpConfigAsync(profileName);
        return dump.ExitCode == 0 && !dump.TimedOut;
    }

    private async Task<(bool Valid, string Message)> ValidateUninstallAsync(
        string profileName, string packageName, string manualName, PluginUninstallKind kind, IProgress<string> log)
    {
        var errors = new List<string>();
        try
        {
            var manifest = ProfileManifestEditor.Load(profileName);
            if (kind is PluginUninstallKind.DependencyWithPatchRows
                or PluginUninstallKind.DependencyWithoutPatchRows
                or PluginUninstallKind.FileOrLinkDependency)
            {
                if (manifest.HasDependency(packageName))
                    errors.Add($"dependencies 仍包含 {packageName}");
                if (manifest.HasBundle(packageName))
                    errors.Add($"dsh.profile.bundles 仍包含 {packageName}");
            }
            if (kind == PluginUninstallKind.BundleListedOnly && manifest.HasBundle(packageName))
                errors.Add($"dsh.profile.bundles 仍包含 {packageName}");
        }
        catch (Exception ex)
        {
            errors.Add("package.json 不可解析：" + ex.Message);
        }

        errors.AddRange(await CollectPatchAndDumpErrorsAsync(profileName, doc =>
        {
            var lingering = doc.TopLevelEntries
                .SelectMany(e => e.IsInsertList ? e.InsertedRows : [e])
                .Any(e => e.Name != null
                    && (e.Name == packageName
                        || e.Name == manualName
                        || (packageName.Length > 0 && PluginInventory.PackageOf(e.Name) == packageName)));
            return lingering ? "仍有引用行" : null;
        }));

        return Summarize(errors);
    }

    private static void AddManifestParseError(string profileName, List<string> errors)
    {
        try
        {
            _ = ProfileManifestEditor.Load(profileName);
        }
        catch (Exception ex)
        {
            errors.Add("package.json 不可解析：" + ex.Message);
        }
    }

    private static string[] PatchPathsFor(string profileName) =>
        [DshPaths.ProfilePatchPath(profileName), DshPaths.HomePatchPath];

    private async Task<List<string>> CollectPatchAndDumpErrorsAsync(
        string profileName, Func<PatchDocument, string?>? extraPatchCheck = null)
    {
        var errors = new List<string>();
        foreach (var patchPath in PatchPathsFor(profileName))
        {
            if (!File.Exists(patchPath))
                continue;
            try
            {
                var doc = PatchDocument.Load(patchPath);
                if (!doc.ValidateStructure(out var error))
                    errors.Add($"{patchPath} 结构异常：{error}");
                else if (extraPatchCheck?.Invoke(doc) is { } extra)
                    errors.Add($"{patchPath} {extra}");
            }
            catch (Exception ex)
            {
                errors.Add($"{patchPath} 不可解析：" + ex.Message);
            }
        }

        if (DshCliService.IsInstalled)
        {
            var dump = await DshCliService.DumpConfigAsync(profileName);
            if (dump.ExitCode != 0)
                errors.Add("dump-config 失败：" + TrimOutput(dump.Output));
        }

        return errors;
    }

    private static (bool Valid, string Message) Summarize(List<string> errors) =>
        errors.Count == 0
            ? (true, "")
            : (false, string.Join("\n", errors));

    // ------------------------------------------------------------------
    // 暂存 / 撤销
    // ------------------------------------------------------------------

    private sealed class ExternalBackup
    {
        public string Name { get; set; } = "";

        public string OriginalPath { get; set; } = "";

        public bool IsJunction { get; set; }

        public string LinkTarget { get; set; } = "";
    }

    private sealed class UninstallRecordData
    {
        public string RecordId { get; set; } = "";

        public string ProfileName { get; set; } = "";

        public string PackageName { get; set; } = "";

        public string ManualName { get; set; } = "";

        public DateTime ExecutedAt { get; set; }

        public bool RestoreHomePatch { get; set; }

        public Dictionary<string, string> HashAfter { get; set; } = new(StringComparer.Ordinal);

        public List<ExternalBackup> ExternalBackups { get; set; } = [];
    }

    private async Task<string> StageUninstallAsync(
        PluginProfileSnapshot snapshot,
        string packageName,
        string manualName,
        bool homeInvolved,
        List<ExternalBackup> externalBackups,
        IProgress<string> log)
    {
        var sequence = Interlocked.Increment(ref _undoSequence);
        var recordDir = Path.Combine(SessionRoot, sequence.ToString("000"));
        var backupDir = Path.Combine(recordDir, "backup");
        Directory.CreateDirectory(backupDir);

        log.Report($"暂存 profile 完整副本：{backupDir}\\profile");
        DshFileSystem.CopyDirectory(snapshot.ProfileDirectory, Path.Combine(backupDir, "profile"), log);

        var restoreHomePatch = homeInvolved && File.Exists(snapshot.HomePatchPath);
        if (restoreHomePatch)
        {
            var homeBackup = Path.Combine(backupDir, "home-patch.yml");
            File.Copy(snapshot.HomePatchPath, homeBackup, overwrite: true);
            log.Report($"暂存 home patch：{homeBackup}");
        }

        var data = new UninstallRecordData
        {
            RecordId = Path.GetFileName(recordDir),
            ProfileName = snapshot.ProfileName,
            PackageName = packageName,
            ManualName = manualName,
            ExecutedAt = DateTime.Now,
            RestoreHomePatch = restoreHomePatch,
            ExternalBackups = externalBackups,
        };
        await SaveRecordAsync(recordDir, data);
        return recordDir;
    }

    private async Task<Dictionary<string, string>> BuildCurrentHashesAsync(
        PluginProfileSnapshot snapshot,
        bool homeInvolved,
        List<ExternalBackup> externalBackups)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (relative, hash) in DshFileSystem.HashTree(snapshot.ProfileDirectory))
            hashes["profile/" + relative] = hash;
        if (homeInvolved)
        {
            var homePatch = snapshot.HomePatchPath;
            hashes["home/patch"] = File.Exists(homePatch) ? DshFileSystem.Sha256File(homePatch) : "";
        }
        foreach (var backup in externalBackups)
        {
            if (!Directory.Exists(backup.OriginalPath))
                continue;
            foreach (var (relative, hash) in DshFileSystem.HashTree(backup.OriginalPath))
                hashes["external/" + backup.Name + "/" + relative] = hash;
        }

        return hashes;
    }

    public List<UninstallRecordItem> ListUndoRecords()
    {
        if (!Directory.Exists(SessionRoot))
            return [];
        var records = new List<UninstallRecordItem>();
        foreach (var recordDir in Directory.EnumerateDirectories(SessionRoot))
        {
            var data = LoadRecordOrNull(recordDir);
            if (data == null)
                continue;
            records.Add(new UninstallRecordItem
            {
                RecordId = data.RecordId,
                ProfileName = data.ProfileName,
                PackageName = string.IsNullOrEmpty(data.PackageName) ? data.ManualName : data.PackageName,
                ExecutedAt = data.ExecutedAt,
            });
        }

        return records.OrderByDescending(r => r.ExecutedAt).ToList();
    }

    /// <summary>撤销前检查：返回当前与卸载完成时哈希不一致的文件。</summary>
    public async Task<List<string>> GetUndoChangesAsync(string recordId, IProgress<string> log)
    {
        var recordDir = Path.Combine(SessionRoot, recordId);
        var data = await LoadRecordAsync(recordDir);
        var snapshot = await PluginInventory.LoadAsync(data.ProfileName, log);
        var current = await BuildCurrentHashesAsync(snapshot, data.RestoreHomePatch, data.ExternalBackups);

        return data.HashAfter
            .Where(kv => !current.TryGetValue(kv.Key, out var value) || value != kv.Value)
            .Select(kv => kv.Key)
            .Concat(current.Keys.Where(k => !data.HashAfter.ContainsKey(k)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task<PluginOperationResult> UndoAsync(
        string recordId, bool overwriteChanged, IProgress<string> log)
    {
        var recordDir = Path.Combine(SessionRoot, recordId);
        if (!Directory.Exists(recordDir))
            return PluginOperationResult.Fail("撤销记录不存在，可能已被清理。");

        try
        {
            var changes = await GetUndoChangesAsync(recordId, log);
            if (changes.Count > 0 && !overwriteChanged)
            {
                return PluginOperationResult.Fail(
                    "以下文件在卸载完成后被改动，确认后才会覆盖：\n" + string.Join("\n", changes));
            }
            if (changes.Count > 0)
                log.Report("以下文件已变化，按用户确认覆盖：\n" + string.Join("\n", changes));

            var stop = await StopDshForOperationAsync(log);
            if (!stop.Success)
                return PluginOperationResult.Fail(stop.Message);

            await RestoreBackupAsync(recordDir, overwriteChanged: true, log);

            var data = await LoadRecordAsync(recordDir);
            var validation = await ValidateProfileAsync(data.ProfileName, log);
            if (!validation.Valid)
            {
                return PluginOperationResult.Fail("撤销后复查失败：\n" + validation.Message);
            }

            await RestartManagedDshAsync(log);
            return PluginOperationResult.Ok("已撤销该次卸载并恢复入口与实体，dsh 重启请求已发出");
        }
        catch (Exception ex)
        {
            return PluginOperationResult.Fail("撤销失败：" + ex.Message);
        }
    }

    private async Task RestoreBackupAsync(string recordDir, bool overwriteChanged, IProgress<string> log)
    {
        var data = await LoadRecordAsync(recordDir);
        var backup = Path.Combine(recordDir, "backup");
        var profileBackup = Path.Combine(backup, "profile");
        var profileDir = DshPaths.ProfileDirectory(data.ProfileName);

        log.Report($"删除当前 profile：{profileDir}");
        if (Directory.Exists(profileDir))
            DshFileSystem.DeletePathSafe(profileDir);

        log.Report("放回 profile 暂存副本");
        DshFileSystem.CopyDirectory(profileBackup, profileDir, log);

        if (data.RestoreHomePatch)
        {
            var homeBackup = Path.Combine(backup, "home-patch.yml");
            if (File.Exists(homeBackup))
            {
                log.Report($"放回 home patch：{DshPaths.HomePatchPath}");
                DshFileSystem.WriteAllTextAtomic(
                    DshPaths.HomePatchPath, DshFileSystem.ReadAllTextNoBomSafe(homeBackup));
            }
        }

        foreach (var external in data.ExternalBackups)
        {
            var externalBackup = Path.Combine(backup, "external", external.Name);
            if (!Directory.Exists(externalBackup) && !File.Exists(externalBackup))
                continue;
            log.Report($"放回外部实体：{external.OriginalPath}");
            if (Directory.Exists(external.OriginalPath) || File.Exists(external.OriginalPath))
                DshFileSystem.DeletePathSafe(external.OriginalPath);
            var parent = Path.GetDirectoryName(external.OriginalPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            if (Directory.Exists(externalBackup))
            {
                DshFileSystem.CopyDirectory(externalBackup, external.OriginalPath, log);
            }
            else
            {
                File.Copy(externalBackup, external.OriginalPath, overwrite: true);
            }
        }
    }

    private static readonly JsonSerializerOptions RecordJsonOptions = new() { WriteIndented = true };

    private async Task<UninstallRecordData> LoadRecordAsync(string recordDir)
    {
        var path = Path.Combine(recordDir, "record.json");
        var data = JsonSerializer.Deserialize<UninstallRecordData>(
            await File.ReadAllTextAsync(path), RecordJsonOptions);
        return data ?? throw new InvalidOperationException("撤销记录损坏：" + path);
    }

    private static UninstallRecordData? LoadRecordOrNull(string recordDir)
    {
        try
        {
            var path = Path.Combine(recordDir, "record.json");
            return JsonSerializer.Deserialize<UninstallRecordData>(
                File.ReadAllText(path), RecordJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static Task SaveRecordAsync(string recordDir, UninstallRecordData data)
    {
        var path = Path.Combine(recordDir, "record.json");
        var json = JsonSerializer.Serialize(data, RecordJsonOptions);
        DshFileSystem.WriteAllTextAtomic(path, json);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // dsh 协调
    // ------------------------------------------------------------------

    private async Task<(bool Success, string Message)> StopDshForOperationAsync(IProgress<string> log)
    {
        if (_dsh.IsManagedProcessRunning)
        {
            log.Report("停止 DshGUI 启动的 dsh…");
            _dsh.Stop();
        }

        if (await _dsh.IsServerUpAsync())
        {
            log.Report($"检测到 {_dsh.Port} 端口已有 dsh 服务");
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (!await _dsh.IsServerUpAsync())
                return (true, "");
            await Task.Delay(250);
        }

        return (false, $"{_dsh.Port} 端口仍被占用。存在外部 dsh 实例，已暂停操作；请退出外部实例后重试。");
    }

    private async Task RestartManagedDshAsync(IProgress<string> log)
    {
        log.Report("请求重启 dsh…");
        try
        {
            await _restartDsh();
            log.Report("dsh 重启流程已返回");
        }
        catch (Exception ex)
        {
            log.Report("dsh 重启请求失败：" + ex.Message);
        }
    }

    private static string TrimOutput(string output)
    {
        var lines = output.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(8);
        return string.Join("\n", lines);
    }

    private static void RestoreFile(string backupPath, string targetPath)
    {
        DshFileSystem.DeletePathSafe(targetPath);
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.Copy(backupPath, targetPath, overwrite: true);
    }

    /// <summary>退出时清理本会话的撤销与操作暂存（PLAN 第七节）。</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(SessionRoot))
                DshFileSystem.DeletePathSafe(SessionRoot);
            if (Directory.Exists(MutationRoot))
                DshFileSystem.DeletePathSafe(MutationRoot);
        }
        catch
        {
            // 退出清理失败不影响关闭。
        }
    }
}
