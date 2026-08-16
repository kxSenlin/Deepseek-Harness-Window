using System.Collections.ObjectModel;
using System.Windows.Input;
using DshGUI.Infrastructure;
using DshGUI.Models;
using DshGUI.Services;

namespace DshGUI.ViewModels;

public sealed class PluginManagerViewModel : ViewModelBase
{
    private readonly PluginManagerService _service;
    private readonly PluginPackageService _packageService;
    private readonly DshService _dsh;
    private readonly Func<Task> _restartDsh;
    private readonly HashSet<string> _inventoriedProfiles = new(StringComparer.Ordinal);

    private PluginProfileOption? _selectedProfile;
    private PluginRowItem? _selectedRow;
    private InstalledPackageItem? _selectedPackage;
    private UninstallRecordItem? _selectedUndoRecord;
    private string _filter = "";
    private string _status = "正在初始化…";
    private string _log = "";
    private bool _isBusy;

    public PluginManagerViewModel(
        PluginManagerService service,
        PluginPackageService packageService,
        DshService dsh,
        Func<Task> restartDsh)
    {
        _service = service;
        _packageService = packageService;
        _dsh = dsh;
        _restartDsh = restartDsh;

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        BlockCommand = new RelayCommand(_ => _ = BlockSelectedAsync(), _ => SelectedRow != null);
        UnblockCommand = new RelayCommand(_ => _ = UnblockSelectedAsync(), _ => SelectedRow != null);
        UninstallRowCommand = new RelayCommand(_ => _ = UninstallSelectedRowAsync(), _ => SelectedRow != null);
        UninstallPackageCommand = new RelayCommand(_ => _ = UninstallSelectedPackageAsync(), _ => SelectedPackage != null);
        UndoCommand = new RelayCommand(_ => _ = UndoSelectedAsync(), _ => SelectedUndoRecord != null);
        RestartDshCommand = new RelayCommand(_ => _ = RestartDshAsync());
        ExportPackageCommand = new RelayCommand(_ => _ = ExportPackageAsync());
        ImportPackageCommand = new RelayCommand(_ => _ = ImportPackageAsync());
    }

    public ObservableCollection<PluginProfileOption> Profiles { get; } = [];

    public ObservableCollection<PluginRowItem> Rows { get; } = [];

    public ObservableCollection<InstalledPackageItem> Packages { get; } = [];

    public ObservableCollection<UninstallRecordItem> UndoRecords { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand BlockCommand { get; }

    public ICommand UnblockCommand { get; }

    public ICommand UninstallRowCommand { get; }

    public ICommand UninstallPackageCommand { get; }

    public ICommand UndoCommand { get; }

    public ICommand RestartDshCommand { get; }

    public ICommand ExportPackageCommand { get; }

    public ICommand ImportPackageCommand { get; }

    /// <summary>UI 注入：弹确认框（可要求输入插件名做严格确认）。</summary>
    public Func<PluginConfirmPrompt, bool>? ConfirmCallback { get; set; }

    /// <summary>UI 注入：弹纯提示框（用于“需要停止 dsh”等醒目提示）。</summary>
    public Func<string, string, bool>? NoticeCallback { get; set; }

    /// <summary>UI 注入：弹出带红色“停止 DeepSeek Harness”按钮的对话框；返回 true 表示用户点击了红色按钮。</summary>
    public Func<string, bool>? StopDshRequestedCallback { get; set; }

    /// <summary>UI 注入：选择 .dshpkg 保存路径；返回 null 表示取消。</summary>
    public Func<string?>? ExportPackagePathCallback { get; set; }

    /// <summary>UI 注入：选择 .dshpkg 文件；返回 null 表示取消。</summary>
    public Func<string?>? ImportPackagePathCallback { get; set; }

    /// <summary>UI 注入：显示导入预览并返回目标 Profile 与勾选的插件名；返回 null 表示取消。</summary>
    public Func<PluginImportPreview, PluginImportSelection?>? ImportPreviewCallback { get; set; }

    public PluginProfileOption? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
                _ = RefreshAsync();
        }
    }

    public PluginRowItem? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
                RaiseRowCommands();
        }
    }

    public InstalledPackageItem? SelectedPackage
    {
        get => _selectedPackage;
        set
        {
            if (SetProperty(ref _selectedPackage, value))
                RaisePackageCommands();
        }
    }

    public UninstallRecordItem? SelectedUndoRecord
    {
        get => _selectedUndoRecord;
        set
        {
            if (SetProperty(ref _selectedUndoRecord, value))
                RaiseUndoCommand();
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
                ApplyFilter();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Log
    {
        get => _log;
        private set => SetProperty(ref _log, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                RaiseRowCommands();
        }
    }

    private readonly ObservableCollection<PluginRowItem> _allRows = [];

    public async Task InitializeAsync()
    {
        AppendLog($"DSH_HOME：{DshPaths.DshHome}");
        AppendLog($"dsh：{(DshCliService.IsInstalled ? DshPaths.FindDshCommand() : "未安装")}");
        var profiles = PluginInventory.DiscoverProfiles();
        Profiles.Clear();
        foreach (var profile in profiles)
            Profiles.Add(profile);

        if (Profiles.Count > 0)
        {
            var preferred = Profiles.FirstOrDefault(p => p.Name == "web") ?? Profiles[0];
            SelectedProfile = preferred;
        }
        else
        {
            Status = "未找到 profile（$DSH_HOME/profiles 下没有 package.json）";
        }

        RefreshUndoRecords();
    }

    private async Task RefreshAsync()
    {
        if (SelectedProfile == null)
            return;
        var profileName = SelectedProfile.Name;
        IsBusy = true;
        Status = $"正在盘点 profile {profileName}…";
        try
        {
            var progress = new Progress<string>(AppendLog);
            var snapshot = await PluginInventory.LoadAsync(profileName, progress);
            if (!_inventoriedProfiles.Contains(profileName))
            {
                _service.MarkInventory(snapshot);
                _inventoriedProfiles.Add(profileName);
            }

            _allRows.Clear();
            foreach (var row in snapshot.Rows)
            {
                row.IsExistingPlugin = _service.IsExistingRow(profileName, row.Id);
                _allRows.Add(row);
            }

            ApplyFilter();

            Packages.Clear();
            foreach (var package in snapshot.Packages)
                Packages.Add(package);

            var warningText = snapshot.Warnings.Count > 0
                ? "；警告：" + string.Join("；", snapshot.Warnings)
                : "";
            Status =
                $"{profileName}：{snapshot.Rows.Count} 行插件，{snapshot.Packages.Count} 个依赖"
                + (snapshot.UsedDumpConfig ? "（dump-config 为事实）" : "（离线组合）") + warningText;
            AppendLog($"盘点完成：{snapshot.Rows.Count} 行，{snapshot.Packages.Count} 个依赖");
        }
        catch (Exception ex)
        {
            Status = "盘点失败：" + ex.Message;
            AppendLog("盘点失败：" + ex);
        }
        finally
        {
            IsBusy = false;
            RaiseRowCommands();
        }
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        foreach (var row in _allRows)
        {
            if (string.IsNullOrWhiteSpace(_filter)
                || row.Id.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || row.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || row.OriginLabel.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            {
                Rows.Add(row);
            }
        }
    }

    private async Task ToggleRowAsync(PluginRowItem? row, bool disabled)
    {
        if (row == null || SelectedProfile == null || !CheckIdle())
            return;

        var action = disabled ? "屏蔽" : "解除屏蔽";
        if (disabled && row.Status == PluginRowStatus.Disabled)
        {
            Status = "该行已处于屏蔽状态";
            return;
        }

        var serverUp = await _dsh.IsRunningAsync();
        if (serverUp)
        {
            var warning = disabled && row.IsBuiltInCore
                ? "⚠ 该行是内置核心行，屏蔽后可能导致 dsh 启动失败。\n\n"
                : "";
            if (!await EnsureDshStoppedForOperationAsync(action, warning))
                return;
        }
        else
        {
            var prompt = new PluginConfirmPrompt
            {
                Title = action + "插件行",
                Message = $"确认{action}插件行？\n\nid：{row.Id}\nname：{row.Name}\n"
                    + (disabled && row.IsBuiltInCore
                        ? "该行是内置核心行，屏蔽后可能导致 dsh 启动失败。\n"
                        : "")
                    + $"\n当前 dsh 未运行，将直接执行{action}；完成后请点击「重启 dsh」。",
            };
            if (ConfirmCallback != null && !ConfirmCallback(prompt))
                return;
        }

        await RunOperationAsync($"{action} {row.Id}", _service.SetRowDisabledAsync(
            SelectedProfile.Name, row.Id, disabled, new Progress<string>(AppendLog),
            restartAfter: false));
    }

    private async Task BlockSelectedAsync() =>
        await ToggleRowAsync(SelectedRow, disabled: true);

    private async Task UnblockSelectedAsync() =>
        await ToggleRowAsync(SelectedRow, disabled: false);

    /// <summary>
    /// 检测到 dsh 正在运行时弹红色按钮对话框；用户点击红色按钮后真正停止 dsh。
    /// 停止失败时用纯提示框说明原因。
    /// </summary>
    private async Task<bool> EnsureDshStoppedForOperationAsync(string action, string warning = "")
    {
        if (StopDshRequestedCallback == null)
            return false;

        var message =
            warning
            + $"执行{action}前需要先停止 DeepSeek Harness。\n\n"
            + "点击下方红色按钮将停止运行中的 DeepSeek Harness，然后继续执行操作。\n"
            + "操作完成后请稍后点击「重启 dsh」。";

        if (!StopDshRequestedCallback(message))
            return false;

        IsBusy = true;
        Status = "正在停止 DeepSeek Harness…";
        AppendLog("用户确认停止 DeepSeek Harness");
        try
        {
            var stopped = await _dsh.StopRunningDshAsync(new Progress<string>(AppendLog));
            if (!stopped)
            {
                NoticeCallback?.Invoke("停止失败",
                    "未能停止运行中的 DeepSeek Harness。\n\n"
                    + $"可能 {_dsh.Port} 端口被其他程序占用，请检查后重试。");
            }

            return stopped;
        }
        catch (Exception ex)
        {
            NoticeCallback?.Invoke("停止失败", "停止 DeepSeek Harness 时发生错误：\n" + ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UninstallSelectedRowAsync()
    {
        var row = SelectedRow;
        if (row == null || SelectedProfile == null || !CheckIdle())
            return;

        if (row.UninstallKind == PluginUninstallKind.BuiltIn)
        {
            Status = "内置插件只提供屏蔽，不提供卸载。";
            return;
        }

        var matchText = row.PackageName.Length > 0 ? row.PackageName : row.Name;
        var prompt = new PluginConfirmPrompt
        {
            Title = "卸载插件",
            Message = BuildUninstallMessage(row, matchText),
            RequireNameInput = row.IsExistingPlugin || row.UninstallKind == PluginUninstallKind.ManualPatchOnly,
            NameToMatch = row.IsExistingPlugin || row.UninstallKind == PluginUninstallKind.ManualPatchOnly
                ? matchText
                : "",
        };
        if (ConfirmCallback != null && !ConfirmCallback(prompt))
            return;

        var deleteExternal = row.UninstallKind == PluginUninstallKind.FileOrLinkDependency
            && await ConfirmDeleteExternalAsync(row.EntityDisplay);
        if (row.UninstallKind == PluginUninstallKind.FileOrLinkDependency && !deleteExternal)
            return;

        if (await _dsh.IsRunningAsync()
            && !await EnsureDshStoppedForOperationAsync("卸载插件"))
        {
            return;
        }

        await RunOperationAsync($"卸载 {matchText}", _service.UninstallRowAsync(
            SelectedProfile.Name, row.Id, deleteExternal, new Progress<string>(AppendLog)));
    }

    private async Task UninstallSelectedPackageAsync()
    {
        var package = SelectedPackage;
        if (package == null || SelectedProfile == null || !CheckIdle())
            return;

        var prompt = new PluginConfirmPrompt
        {
            Title = "卸载依赖包",
            Message = $"确认通过官方命令卸载依赖包？\n\n包名：{package.Name}\n来源：{package.Spec}\n"
                + (package.IsBundleListed ? "该包在 dsh.profile.bundles 中，官方卸载会同步移除 bundle 条目。\n" : "")
                + (package.HasPatchRows ? "patch 中的引用行会随后清理。\n" : ""),
            RequireNameInput = _service.IsExistingPackage(SelectedProfile.Name, package.Name),
            NameToMatch = _service.IsExistingPackage(SelectedProfile.Name, package.Name) ? package.Name : "",
        };
        if (ConfirmCallback != null && !ConfirmCallback(prompt))
            return;

        var deleteExternal = package.IsFileOrLink
            && await ConfirmDeleteExternalAsync(
                package.EntityPath.Length > 0 ? package.EntityPath : "实体未找到");
        if (package.IsFileOrLink && !deleteExternal)
            return;

        if (await _dsh.IsRunningAsync()
            && !await EnsureDshStoppedForOperationAsync("卸载依赖包"))
        {
            return;
        }

        await RunOperationAsync($"卸载 {package.Name}", _service.UninstallPackageAsync(
            SelectedProfile.Name, package.Name, deleteExternal, new Progress<string>(AppendLog)));
    }

    private string BuildUninstallMessage(PluginRowItem row, string matchText)
    {
        var message = "确认卸载该插件？\n\n"
            + $"id：{row.Id}\nname：{row.Name}\n来源：{row.OriginLabel}\n"
            + $"实体：{(row.EntityExists ? row.EntityPath + (row.EntityIsJunction ? "（junction）" : "") : "未找到")}\n";
        if (row.IsExistingPlugin)
            message += "\n该插件在首次盘点时已存在，卸载需输入插件名再次确认。\n";
        return message;
    }

    private async Task<bool> ConfirmDeleteExternalAsync(string entityDisplay)
    {
        var prompt = new PluginConfirmPrompt
        {
            Title = "外部源码目录",
            Message = "该插件是 file:/link: 安装。官方卸载只移除 profile 内的依赖记录，"
                + "外部源码目录不会自动删除。\n\n是否同时删除外部实体？\n" + entityDisplay,
            RequireNameInput = false,
        };
        return ConfirmCallback?.Invoke(prompt) ?? false;
    }

    private async Task UndoSelectedAsync()
    {
        var record = SelectedUndoRecord;
        if (record == null || !CheckIdle())
            return;

        try
        {
            var changes = await RunWithBusyAsync(
                "检查撤销冲突…",
                () => _service.GetUndoChangesAsync(record.RecordId, new Progress<string>(AppendLog)));

            var overwrite = false;
            if (changes.Count > 0)
            {
                var prompt = new PluginConfirmPrompt
                {
                    Title = "撤销卸载（文件已变化）",
                    Message = "以下文件在卸载完成后被改动，确认后将以暂存副本覆盖：\n\n"
                        + string.Join("\n", changes.Take(30)),
                };
                if (ConfirmCallback == null || !ConfirmCallback(prompt))
                    return;
                overwrite = true;
            }
            else
            {
                var prompt = new PluginConfirmPrompt
                {
                    Title = "撤销卸载",
                    Message = $"确认撤销「{record.Display}」？\n将停止 dsh、放回暂存副本并重启。",
                };
                if (ConfirmCallback == null || !ConfirmCallback(prompt))
                    return;
            }

            if (await _dsh.IsRunningAsync()
                && !await EnsureDshStoppedForOperationAsync("撤销卸载"))
            {
                return;
            }

            await RunOperationAsync(
                "撤销 " + record.Display,
                _service.UndoAsync(record.RecordId, overwrite, new Progress<string>(AppendLog)));
        }
        catch (Exception ex)
        {
            IsBusy = false;
            Status = "检查撤销冲突失败：" + ex.Message;
            AppendLog("检查撤销冲突异常：" + ex);
        }
    }

    private async Task ExportPackageAsync()
    {
        if (SelectedProfile == null || !CheckIdle())
            return;
        var path = ExportPackagePathCallback?.Invoke();
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (await _dsh.IsRunningAsync()
            && !await EnsureDshStoppedForOperationAsync("导出插件包"))
        {
            return;
        }
        await RunOperationAsync("导出插件包", _packageService.ExportAsync(
            SelectedProfile.Name, path, new Progress<string>(AppendLog)));
    }

    private async Task ImportPackageAsync()
    {
        if (SelectedProfile == null || !CheckIdle())
            return;
        var path = ImportPackagePathCallback?.Invoke();
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var validation = await Task.Run(() => _packageService.ValidatePackage(path));
            if (!validation.Valid)
            {
                NoticeCallback?.Invoke("插件包无效", string.Join("\n", validation.Errors));
                return;
            }

            if (validation.Warnings.Count > 0)
            {
                AppendLog("插件包警告：" + string.Join("；", validation.Warnings));
            }

            var preview = await RunWithBusyAsync(
                "分析插件包…",
                () => _packageService.PreviewImportAsync(SelectedProfile.Name, path));
            if (preview == null)
            {
                NoticeCallback?.Invoke("插件包无效", "无法读取插件包，请确认 .dshpkg 文件未损坏。");
                return;
            }

            preview.AvailableProfiles = Profiles.Select(p => p.Name).ToList();

            string targetProfile;
            IReadOnlyList<string> selectedNames;
            if (ImportPreviewCallback != null)
            {
                var selection = ImportPreviewCallback(preview);
                if (selection == null)
                    return;
                targetProfile = selection.ProfileName;
                selectedNames = selection.SelectedNames;
            }
            else
            {
                var prompt = new PluginConfirmPrompt
                {
                    Title = "导入插件包",
                    Message = "导入只会新增当前缺失的插件，已存在的插件不会替换。是否继续？",
                };
                if (ConfirmCallback != null && !ConfirmCallback(prompt))
                    return;
                targetProfile = SelectedProfile.Name;
                selectedNames = preview.Additions;
            }

            if (string.IsNullOrWhiteSpace(targetProfile) || selectedNames.Count == 0)
            {
                NoticeCallback?.Invoke("导入信息不完整", "请选择目标 Profile，并勾选要导入的插件。");
                return;
            }

            // 与屏蔽/卸载复用：dsh 运行时先弹红色停止按钮，再断开。
            if (await _dsh.IsRunningAsync()
                && !await EnsureDshStoppedForOperationAsync("导入插件包"))
            {
                return;
            }

            await RunOperationAsync("导入插件包", _packageService.ImportAsync(
                targetProfile, path, selectedNames, new Progress<string>(AppendLog)));
        }
        catch (Exception ex)
        {
            IsBusy = false;
            Status = "分析插件包失败：" + ex.Message;
            AppendLog("分析插件包异常：" + ex);
        }
    }

    private async Task RestartDshAsync()
    {
        if (!CheckIdle())
            return;
        await RunOperationAsync("重启 dsh", RestartViaCallbackAsync());
    }

    private async Task<PluginOperationResult> RestartViaCallbackAsync()
    {
        await _restartDsh();
        return PluginOperationResult.Ok("dsh 重启流程已执行");
    }

    private async Task RunOperationAsync(
        string title, Task<PluginOperationResult> operation)
    {
        IsBusy = true;
        Status = title + " 进行中…";
        AppendLog("—— " + title + " ——");
        try
        {
            var result = await operation;
            AppendLog(result.Success ? "完成：" + result.Message : "失败：" + result.Message);
            await RefreshAsync();
            // 保留操作结果（例如“请稍后点击重启 dsh”），刷新只更新列表数据。
            Status = result.Message;
        }
        catch (Exception ex)
        {
            Status = title + "失败：" + ex.Message;
            AppendLog(title + "异常：" + ex);
        }
        finally
        {
            IsBusy = false;
            RefreshUndoRecords();
        }
    }

    private async Task<T> RunWithBusyAsync<T>(string status, Func<Task<T>> action)
    {
        IsBusy = true;
        Status = status;
        try
        {
            return await action();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RefreshUndoRecords()
    {
        UndoRecords.Clear();
        foreach (var record in _service.ListUndoRecords())
            UndoRecords.Add(record);
        RaiseUndoCommand();
    }

    private bool CheckIdle()
    {
        if (IsBusy)
        {
            Status = "当前有操作正在进行，请稍候。";
            return false;
        }

        return true;
    }

    private void RaiseRowCommands()
    {
        (BlockCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UnblockCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UninstallRowCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RaisePackageCommands() =>
        (UninstallPackageCommand as RelayCommand)?.RaiseCanExecuteChanged();

    private void RaiseUndoCommand() =>
        (UndoCommand as RelayCommand)?.RaiseCanExecuteChanged();

    private void AppendLog(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        _log += $"[{stamp}] {line}\n";
        if (_log.Length > 40_000)
            _log = _log[^30_000..];
        OnPropertyChanged(nameof(Log));
    }
}
