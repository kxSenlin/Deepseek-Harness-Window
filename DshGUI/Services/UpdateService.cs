using System.Net.Http;
using System.Text.Json;

namespace DshGUI.Services;

public sealed class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<string?> GetLatestVersionAsync(string registry)
    {
        try
        {
            var url = registry.TrimEnd('/') + "/@deepseek-ai/dsh/latest";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("version").GetString();
        }
        catch
        {
            return null;
        }
    }

    public static string? GetInstalledVersion()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var packageJson = Path.Combine(appData, "npm", "node_modules", "@deepseek-ai", "dsh", "package.json");
            if (!File.Exists(packageJson))
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
        return l != null && i != null && l > i;
    }

    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var dash = text.IndexOf('-');
        if (dash >= 0)
            text = text[..dash];

        return Version.TryParse(text, out var version) ? version : null;
    }
}
