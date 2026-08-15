namespace DshGUI.Models;

/// <summary>插件行首次插入时所在的配置层。</summary>
public enum PluginRowOrigin
{
    /// <summary>随 dsh 安装的内置 bundle（@deepseek-ai/dsh-base 等）。</summary>
    BuiltInBundle,

    /// <summary>profile 依赖并列入 dsh.profile.bundles 的第三方 bundle。</summary>
    ProfileBundle,

    /// <summary>profiles/&lt;name&gt;/cordis.patch.yml 里手工 insert 的行。</summary>
    ProfilePatchInsert,

    /// <summary>$DSH_HOME/cordis.patch.yml 里手工 insert 的行。</summary>
    HomePatchInsert,

    /// <summary>无法判定来源（仅当 dump-config 输出出现未知来源时）。</summary>
    Unknown,
}

/// <summary>插件行状态。</summary>
public enum PluginRowStatus
{
    Enabled,
    Disabled,
    Expression,
}

/// <summary>卸载时按入口状态分类。</summary>
public enum PluginUninstallKind
{
    /// <summary>dependencies 中有记录，patch 中有引用它的行。</summary>
    DependencyWithPatchRows,

    /// <summary>dependencies 中有记录，patch 中无引用行（普通依赖）。</summary>
    DependencyWithoutPatchRows,

    /// <summary>仅存在于 patch 手工行。</summary>
    ManualPatchOnly,

    /// <summary>仅在 dsh.profile.bundles 中，dependencies 无记录。</summary>
    BundleListedOnly,

    /// <summary>内置行，只允许屏蔽。</summary>
    BuiltIn,

    /// <summary>file:/link: 依赖。</summary>
    FileOrLinkDependency,
}

/// <summary>操作台展示的一条插件行。</summary>
public sealed class PluginRowItem
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public PluginRowOrigin Origin { get; init; }

    public string OriginLabel { get; init; } = "";

    public string SourceFile { get; init; } = "";

    /// <summary>实体目录或模块位置；未找到时为空。</summary>
    public string EntityPath { get; init; } = "";

    public bool EntityExists { get; init; }

    public bool EntityIsJunction { get; init; }

    public PluginRowStatus Status { get; init; }

    public string DisabledRaw { get; init; } = "";

    /// <summary>该行首次插入层自带的 disabled 值（未受用户 patch 影响）。</summary>
    public string OriginDisabledRaw { get; init; } = "";

    public bool? OriginDisabled { get; init; }

    /// <summary>对应 dependencies 里的包名；非依赖行时为空。</summary>
    public string PackageName { get; init; } = "";

    public string DependencySpec { get; init; } = "";

    public PluginUninstallKind UninstallKind { get; init; }

    /// <summary>首次盘点时已存在（用于卸载严格确认）。</summary>
    public bool IsExistingPlugin { get; set; }

    /// <summary>内置核心行屏蔽可能导致 dsh 无法启动。</summary>
    public bool IsBuiltInCore => Origin == PluginRowOrigin.BuiltInBundle;

    public string StatusDisplay => Status switch
    {
        PluginRowStatus.Disabled => "屏蔽",
        PluginRowStatus.Expression => "表达式",
        _ => "启用",
    };

    public string EntityDisplay => EntityExists
        ? EntityPath + (EntityIsJunction ? "（junction）" : "")
        : string.IsNullOrEmpty(EntityPath) ? "未找到" : EntityPath + "（不存在）";

    public string SourceDisplay => OriginLabel;

    public string PackageDisplay => string.IsNullOrEmpty(DependencySpec)
        ? PackageName
        : $"{PackageName} ({DependencySpec})";
}

/// <summary>Profile 选项。</summary>
public sealed class PluginProfileOption
{
    public string Name { get; init; } = "";

    public override string ToString() => Name;
}

/// <summary>会话内的卸载撤销记录。</summary>
public sealed class UninstallRecordItem
{
    public string RecordId { get; init; } = "";

    public string ProfileName { get; init; } = "";

    public string PackageName { get; init; } = "";

    public DateTime ExecutedAt { get; init; }

    public string Display => $"{ExecutedAt:HH:mm:ss}  {ProfileName} / {PackageName}";
}

/// <summary>操作结果。</summary>
public sealed class PluginOperationResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = "";

    public static PluginOperationResult Ok(string message) =>
        new() { Success = true, Message = message };

    public static PluginOperationResult Fail(string message) =>
        new() { Success = false, Message = message };
}

/// <summary>需要用户确认的提示。</summary>
public sealed class PluginConfirmPrompt
{
    public string Title { get; init; } = "";

    public string Message { get; init; } = "";

    /// <summary>是否要求输入插件名进行严格确认。</summary>
    public bool RequireNameInput { get; init; }

    /// <summary>严格确认时要求输入的文本（插件名或行 id）。</summary>
    public string NameToMatch { get; init; } = "";
}
