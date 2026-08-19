using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DshGUI.Services;

public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DshGUI";
    private const string ShortcutFileName = "DshGUI.lnk";

    public static void SetEnabled(bool enabled)
    {
        // 老版本可能写过注册表 Run，统一先清掉，保证自启只走 Startup 文件夹快捷方式。
        RemoveLegacyRunKey();

        if (enabled)
            CreateStartupShortcut();
        else
            DeleteStartupShortcut();
    }

    /// <summary>
    /// 迁移旧版本的注册表自启项：删除 HKCU\...\Run 下名为 DshGUI 的值；
    /// 若用户仍开启自启，则改建 Startup 文件夹快捷方式。
    /// </summary>
    public static void MigrateLegacyRunKey(bool autoStartEnabled)
    {
        var existed = RemoveLegacyRunKey();
        if (existed && autoStartEnabled)
            CreateStartupShortcut();
    }

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutFileName);

    private static bool RemoveLegacyRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) == null)
                return false;

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool CreateStartupShortcut()
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var (target, arguments, workingDirectory, iconLocation) = ResolveStartupCommand();
            Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return false;

            shell = Activator.CreateInstance(shellType);
            if (shell == null)
                return false;

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                [ShortcutPath]);
            if (shortcut == null)
                return false;

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [target]);
            shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, [arguments]);
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [workingDirectory]);
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [iconLocation]);
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, ["DeepSeek Harness 启动器"]);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (shortcut != null)
                Marshal.ReleaseComObject(shortcut);
            if (shell != null)
                Marshal.ReleaseComObject(shell);
        }
    }

    private static void DeleteStartupShortcut()
    {
        try
        {
            if (File.Exists(ShortcutPath))
                File.Delete(ShortcutPath);
        }
        catch
        {
            // 快捷方式可能被占用（例如资源管理器正在读），忽略。
        }
    }

    /// <summary>
    /// 解析自启目标。优先使用当前进程的 apphost exe；
    /// 若当前进程是 dotnet.exe（例如通过 `dotnet DshGUI.dll` 启动），则退回用入口 DLL。
    /// </summary>
    private static (string Target, string Arguments, string WorkingDirectory, string IconLocation) ResolveStartupCommand()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var appDir = Path.GetDirectoryName(processPath) ?? "";
            return (processPath, "--autostart", appDir, processPath + ",0");
        }

        // dotnet 启动的兜底：优先找同目录下的 DshGUI.exe apphost，找不到才用 dotnet + DLL。
        var assemblyName = Assembly.GetExecutingAssembly().GetName().Name ?? "DshGUI";
        var assemblyDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var assemblyPath = Path.Combine(assemblyDir, assemblyName + ".dll");
        var appHost = Path.Combine(assemblyDir, assemblyName + ".exe");
        if (File.Exists(appHost))
            return (appHost, "--autostart", assemblyDir, appHost + ",0");

        var dotnet = processPath;
        if (string.IsNullOrWhiteSpace(dotnet))
            dotnet = DshPaths.FindOnPath("dotnet") ?? "dotnet.exe";

        return (dotnet, $"\"{assemblyPath}\" --autostart", assemblyDir, assemblyPath + ",0");
    }
}
