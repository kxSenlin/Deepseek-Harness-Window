using System.Net.Http;
using System.Text.Json;

namespace DshGUI.Services;

public sealed class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// 读取 npm dist-tags 中的 latest 与 next：
    /// latest 作为首选更新通道，next 作为可选预览版通道。
    /// </summary>
    public async Task<(string? Latest, string? Preview)> GetAvailableVersionsAsync(string registry)
    {
        try
        {
            var url = registry.TrimEnd('/') + "/@deepseek-ai/dsh";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            string? latest = null;
            string? preview = null;
            if (doc.RootElement.TryGetProperty("dist-tags", out var distTags)
                && distTags.ValueKind == JsonValueKind.Object)
            {
                if (distTags.TryGetProperty("latest", out var latestElement))
                    latest = latestElement.GetString();
                if (distTags.TryGetProperty("next", out var nextElement))
                    preview = nextElement.GetString();
            }

            return (latest, preview);
        }
        catch
        {
            return (null, null);
        }
    }

    public static string? GetInstalledVersion()
    {
        try
        {
            var packageJson = FindInstalledPackageJson();
            if (packageJson == null || !File.Exists(packageJson))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            return doc.RootElement.GetProperty("version").GetString();
        }
        catch
        {
            return null;
        }
    }

    public static bool IsNewer(string? latest, string? installed)
    {
        var l = ParseVersion(latest);
        var i = ParseVersion(installed);
        return l != null && i != null && l.CompareTo(i) > 0;
    }

    private static string? FindInstalledPackageJson()
    {
        // 优先按 PATH 上的 dsh.cmd 反查 npm 全局安装目录（兼容 nvm / 自定义 prefix）。
        var installRoot = DshPaths.FindDshInstallRoot();
        if (installRoot != null)
        {
            var packageJson = Path.Combine(installRoot, "package.json");
            if (File.Exists(packageJson))
                return packageJson;
        }

        // 兜底：Node 安装包默认的 npm 全局目录。
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var legacy = Path.Combine(appData, "npm", "node_modules", "@deepseek-ai", "dsh", "package.json");
        return File.Exists(legacy) ? legacy : null;
    }

    private static SemVer? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        // 构建元数据不参与优先级比较：1.2.3+build 等同 1.2.3。
        var plus = text.IndexOf('+');
        if (plus >= 0)
            text = text[..plus];

        string? prerelease = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];
        }

        var core = text.Split('.');
        if (core.Length != 3
            || !int.TryParse(core[0], out var major)
            || !int.TryParse(core[1], out var minor)
            || !int.TryParse(core[2], out var patch))
        {
            return null;
        }

        return new SemVer(major, minor, patch, prerelease);
    }

    /// <summary>SemVer 2.0 子集：比较 1.2.3-rc.7 这类 npm 版本。</summary>
    private sealed class SemVer : IComparable<SemVer>
    {
        private readonly int _major;
        private readonly int _minor;
        private readonly int _patch;
        private readonly string[] _prerelease;

        public SemVer(int major, int minor, int patch, string? prerelease)
        {
            _major = major;
            _minor = minor;
            _patch = patch;
            _prerelease = string.IsNullOrEmpty(prerelease)
                ? []
                : prerelease.Split('.');
        }

        public int CompareTo(SemVer? other)
        {
            if (other == null)
                return 1;

            var c = _major.CompareTo(other._major);
            if (c != 0)
                return c;
            c = _minor.CompareTo(other._minor);
            if (c != 0)
                return c;
            c = _patch.CompareTo(other._patch);
            if (c != 0)
                return c;

            var a = _prerelease;
            var b = other._prerelease;
            if (a.Length == 0 && b.Length == 0)
                return 0;
            // 没有预发布号 > 有预发布号：1.2.3 > 1.2.3-rc.1。
            if (a.Length == 0)
                return 1;
            if (b.Length == 0)
                return -1;

            var count = Math.Max(a.Length, b.Length);
            for (var i = 0; i < count; i++)
            {
                if (i >= a.Length)
                    return -1;
                if (i >= b.Length)
                    return 1;

                var ai = a[i];
                var bi = b[i];
                var aIsNumber = int.TryParse(ai, out var aNumber);
                var bIsNumber = int.TryParse(bi, out var bNumber);

                if (aIsNumber && bIsNumber)
                {
                    c = aNumber.CompareTo(bNumber);
                    if (c != 0)
                        return c;
                }
                else if (aIsNumber)
                {
                    return -1;
                }
                else if (bIsNumber)
                {
                    return 1;
                }
                else
                {
                    c = string.Compare(ai, bi, StringComparison.Ordinal);
                    if (c != 0)
                        return c;
                }
            }

            return 0;
        }
    }
}
