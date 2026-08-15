namespace DshGUI.Services;

/// <summary>dsh 相关路径：DSH_HOME、profile 目录、dsh 安装根、Node 解析候选目录。</summary>
public static class DshPaths
{
    public const string HomePatchFileName = "cordis.patch.yml";
    public const string ProfilePatchFileName = "cordis.patch.yml";

    /// <summary>解析 dsh home：$DSH_HOME（非空白）优先，否则 ~/.dsh。</summary>
    public static string DshHome
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(ExpandHome(configured));
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }
    }

    public static string HomePatchPath => Path.Combine(DshHome, HomePatchFileName);

    public static string ProfilesDirectory => Path.Combine(DshHome, "profiles");

    public static string ProfileDirectory(string profileName) =>
        Path.Combine(ProfilesDirectory, profileName);

    public static string ProfilePackageJson(string profileName) =>
        Path.Combine(ProfileDirectory(profileName), "package.json");

    public static string ProfilePatchPath(string profileName) =>
        Path.Combine(ProfileDirectory(profileName), ProfilePatchFileName);

    public static string ExpandHome(string path)
    {
        if (path == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal)
            || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        return path;
    }

    /// <summary>在 PATH 上查找可执行命令（cmd/bat/exe，cmd.exe 可执行的形式）。</summary>
    public static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in new[] { ".cmd", ".bat", ".exe" })
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>在 PATH 上查找 dsh 命令。</summary>
    public static string? FindDshCommand() => FindOnPath("dsh");

    /// <summary>dsh 安装根：npm 前缀下的 @deepseek-ai/dsh；也兼容自定义目录。</summary>
    public static string? FindDshInstallRoot()
    {
        var command = FindDshCommand();
        if (command == null)
            return null;

        // npm 全局安装：<prefix>\dsh.cmd → <prefix>\node_modules\@deepseek-ai\dsh。
        var npmPrefix = Path.GetDirectoryName(command);
        if (npmPrefix != null)
        {
            var expected = Path.Combine(npmPrefix, "node_modules", "@deepseek-ai", "dsh");
            if (File.Exists(Path.Combine(expected, "package.json")))
                return expected;
        }

        // 非 npm 布局：沿目录向上寻找包名为 @deepseek-ai/dsh 的 package.json。
        var dir = npmPrefix;
        while (dir != null)
        {
            var packageJson = Path.Combine(dir, "package.json");
            if (File.Exists(packageJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(packageJson));
                    if (doc.RootElement.TryGetProperty("name", out var name)
                        && name.GetString() == "@deepseek-ai/dsh")
                    {
                        return dir;
                    }
                }
                catch
                {
                    // 不是 JSON，继续向上。
                }
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent == dir)
                break;
            dir = parent;
        }

        return null;
    }

    /// <summary>解析裸模块名到实体目录（近似 Node 的逐级向上查找）。</summary>
    public static string? ResolveModuleDirectory(string specifier, string profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(specifier))
            return null;

        if (specifier.StartsWith("cordis:", StringComparison.Ordinal))
            return null;

        if (specifier.StartsWith('.') || Path.IsPathRooted(specifier))
        {
            var direct = specifier.StartsWith('.')
                ? Path.GetFullPath(Path.Combine(profileDirectory, specifier))
                : specifier;
            return Directory.Exists(direct) ? direct : null;
        }

        // 包名部分：@scope/pkg 取前两段，普通包取第一段；子路径保留在原说明里。
        var parts = specifier.Split('/');
        var packageName = specifier.StartsWith('@') && parts.Length >= 2
            ? parts[0] + "/" + parts[1]
            : parts[0];

        foreach (var root in NodeSearchRoots(profileDirectory))
        {
            var candidate = Path.Combine(root, packageName);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> NodeSearchRoots(string profileDirectory)
    {
        yield return Path.Combine(profileDirectory, "node_modules");
        yield return Path.Combine(ProfilesDirectory, "node_modules");
        yield return Path.Combine(DshHome, "node_modules");

        var installRoot = FindDshInstallRoot();
        if (installRoot != null)
        {
            yield return Path.Combine(installRoot, "node_modules");
            // installRoot = <prefix>\node_modules\@deepseek-ai\dsh。
            var npmPrefix = Directory.GetParent(installRoot)?.Parent?.Parent?.FullName;
            if (npmPrefix != null)
                yield return Path.Combine(npmPrefix, "node_modules");
        }

        // 从 profile 目录逐级向上的标准 node_modules。
        var dir = profileDirectory;
        while (dir != null)
        {
            yield return Path.Combine(dir, "node_modules");
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir)
                break;
            dir = parent;
        }
    }
}
