using System.Security.Cryptography;
using System.Text;

namespace DshGUI.Services;

/// <summary>
/// 文件系统操作规则（PLAN 第八节）：
/// junction 先识别再删除（只删链接本身）、写入文件先删旧目标避免改写 hardlink、
/// 配置文件无 BOM UTF-8、临时文件 + 替换。
/// </summary>
public static class DshFileSystem
{
    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取 junction/symlink 的目标；普通目录返回 null。相对目标按链接所在目录归一。</summary>
    public static string? GetLinkTarget(string path)
    {
        try
        {
            var target = IsDirectory(path) ? new DirectoryInfo(path).LinkTarget : new FileInfo(path).LinkTarget;
            if (string.IsNullOrEmpty(target))
                return null;
            if (!Path.IsPathRooted(target))
            {
                var parent = Path.GetDirectoryName(path);
                if (parent != null)
                    target = Path.GetFullPath(Path.Combine(parent, target));
                else
                    target = Path.GetFullPath(target);
            }

            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>安全删除文件/目录：reparse point 目录只删链接本身。</summary>
    public static void DeletePathSafe(string path)
    {
        if (IsReparsePoint(path))
        {
            if (IsDirectory(path))
            {
                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }

            return;
        }

        if (IsDirectory(path))
        {
            DeleteDirectoryRecursive(path);
            return;
        }

        if (File.Exists(path))
            File.Delete(path);
    }

    private static void DeleteDirectoryRecursive(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            DeletePathSafe(entry);
        }

        Directory.Delete(path, recursive: false);
    }

    /// <summary>无 BOM UTF-8 临时文件 + 替换。</summary>
    public static void WriteAllTextAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temp = path + ".dshgui-tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public static string ReadAllTextNoBomSafe(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>复制目录。junction 不递归跟随（避免复制 pnpm store），只复制链接本身的内容到目标。</summary>
    public static void CopyDirectory(string sourceDir, string destDir, IProgress<string>? log = null)
    {
        var internalJunctions = new List<(string LinkPath, string TargetPath)>();
        CopyDirectoryCore(sourceDir, destDir, sourceDir, destDir, internalJunctions, log);

        // 目录树内互相指向的 junction：先复制全部内容，再按相对位置重建链接。
        foreach (var (linkPath, targetPath) in internalJunctions)
        {
            try
            {
                if (!Directory.Exists(targetPath))
                    continue;
                if (Directory.Exists(linkPath))
                    Directory.Delete(linkPath, recursive: true);
                Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
                if (!TryCreateJunction(linkPath, targetPath))
                {
                    // 没有创建 junction 权限时，用已复制的目标内容补回实体目录。
                    CopyDirectory(targetPath, linkPath, log);
                }
            }
            catch
            {
                // 无法建链接时保留已复制的普通目录内容。
            }
        }
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/c mklink /J \"" + linkPath + "\" \"" + targetPath + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
                return false;
            process.WaitForExit(5_000);
            return process.ExitCode == 0 && IsReparsePoint(linkPath);
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectoryCore(
        string sourceDir,
        string destDir,
        string rootSource,
        string rootDest,
        List<(string LinkPath, string TargetPath)> internalJunctions,
        IProgress<string>? log)
    {
        Directory.CreateDirectory(destDir);
        foreach (var source in Directory.EnumerateFileSystemEntries(sourceDir))
        {
            var name = Path.GetFileName(source);
            var dest = Path.Combine(destDir, name);
            if (IsReparsePoint(source))
            {
                var target = GetLinkTarget(source);
                if (target != null && IsPathUnder(rootSource, target))
                {
                    // 目标在本目录树内：留占位，整个树复制完后重建 junction。
                    Directory.CreateDirectory(dest);
                    var mappedTarget = MapPath(rootSource, rootDest, target);
                    internalJunctions.Add((dest, mappedTarget));
                    log?.Report($"junction {name} → {mappedTarget}（复制完成后重建）");
                }
                else if (target != null && Directory.Exists(target))
                {
                    // 目标在树外：复制目标内容为独立副本（PLAN 规则）。
                    CopyDirectory(target, dest, log);
                    log?.Report($"junction {name} → {target}（外部目标，已作为独立副本暂存）");
                }
                else if (target != null && File.Exists(target))
                {
                    CopyFileBytes(target, dest);
                }
                else
                {
                    Directory.CreateDirectory(dest);
                }

                continue;
            }

            if (IsDirectory(source))
            {
                CopyDirectoryCore(source, dest, rootSource, rootDest, internalJunctions, log);
            }
            else
            {
                CopyFileBytes(source, dest);
            }
        }
    }

    private static string MapPath(string rootSource, string rootDest, string target)
    {
        var relative = Path.GetRelativePath(rootSource, target);
        return Path.Combine(rootDest, relative);
    }

    public static bool IsPathUnder(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidateFull = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidateFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(rootFull, candidateFull, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyFileBytes(string source, string dest)
    {
        var directory = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(dest))
            File.Delete(dest);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    public static string Sha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>递归计算目录树哈希（普通文件；junction 不展开，记录链接元数据）。</summary>
    public static Dictionary<string, string> HashTree(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
            return result;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            result[Path.GetRelativePath(root, file)] = Sha256File(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = true,
                 }))
        {
            if (!IsReparsePoint(directory))
                continue;
            result[Path.GetRelativePath(root, directory)] = "junction:" + (GetLinkTarget(directory) ?? "");
        }

        return result;
    }
}
