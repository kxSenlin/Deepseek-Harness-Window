using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DshGUI.Services;

/// <summary>对 profiles/&lt;name&gt;/package.json 做最小侵入编辑。</summary>
public sealed class ProfileManifestEditor
{
    private readonly string _path;
    private readonly JsonObject _root;

    private ProfileManifestEditor(string path, JsonObject root)
    {
        _path = path;
        _root = root;
    }

    public static ProfileManifestEditor Load(string profileName)
    {
        var path = DshPaths.ProfilePackageJson(profileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("profile package.json 不存在", path);

        var root = JsonNode.Parse(DshFileSystem.ReadAllTextNoBomSafe(path)) as JsonObject
            ?? throw new InvalidOperationException("package.json 顶层不是对象：" + path);
        return new ProfileManifestEditor(path, root);
    }

    private IReadOnlyList<string> BundleNames
    {
        get
        {
            if (_root["dsh"]?["profile"]?["bundles"] is not JsonArray bundles)
                return [];
            return bundles.Where(n => n != null).Select(n => n!.GetValue<string>()).ToList();
        }
    }

    public bool HasDependency(string name) =>
        _root["dependencies"] is JsonObject deps && deps.ContainsKey(name);

    public bool HasBundle(string name) => BundleNames.Contains(name);

    public void RemoveDependency(string name)
    {
        if (_root["dependencies"] is JsonObject deps)
            deps.Remove(name);
    }

    public void RemoveBundle(string name)
    {
        if (_root["dsh"] is not JsonObject dsh || dsh["profile"] is not JsonObject profile
            || profile["bundles"] is not JsonArray bundles)
        {
            return;
        }

        var remaining = new JsonArray();
        foreach (var item in bundles)
        {
            if (item != null && item.GetValue<string>() != name)
                remaining.Add(item.GetValue<string>());
        }

        profile["bundles"] = remaining;
    }

    public bool Validate()
    {
        try
        {
            using var doc = JsonDocument.Parse(ToJson());
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    public void Save()
    {
        if (!Validate())
            throw new InvalidOperationException("package.json 校验失败，拒绝写入：" + _path);
        DshFileSystem.WriteAllTextAtomic(_path, ToJson());
    }

    public string ToJson()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return _root.ToJsonString(options) + "\n";
    }
}
