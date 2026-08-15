using System.Text;
using System.Text.RegularExpressions;

namespace DshGUI.Services;

/// <summary>
/// cordis.patch.yml 的最小行级解析器。它只提取管理功能需要的结构：
/// 顶层 patch 条目、insert 列表、以及条目里的 id/name/disabled 标量，
/// 其余内容（config、!!js、注释）一律保留原行，编辑时只增删目标行。
/// 每次修改后都会重建索引，因此任何方法返回的 <see cref="PatchEntry"/>
/// 都是修改后的最新视图，旧引用不应继续使用。
/// </summary>
public sealed partial class PatchDocument
{
    private List<string> _lines;

    private PatchDocument(string path, string content)
    {
        Path = path;
        _lines = NormalizeLines(content);
        TopLevelEntries = ScanList(0, 0, _lines.Count);
    }

    public string Path { get; }

    public List<PatchEntry> TopLevelEntries { get; private set; }

    public static PatchDocument Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("patch 文件不存在", path);
        return new PatchDocument(path, File.ReadAllText(path, Encoding.UTF8));
    }

    public static PatchDocument CreateEmpty(string path) => new(path, "[]\n");

    private static List<string> NormalizeLines(string content) =>
        content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    /// <summary>从 index 开始、在 limit 前，扫描缩进为 listIndent 的条目列表。</summary>
    private List<PatchEntry> ScanList(int listIndent, int fromIndex, int limit)
    {
        var entries = new List<PatchEntry>();
        var i = fromIndex;
        while (i < limit)
        {
            var match = DashLineRegex().Match(_lines[i]);
            if (!match.Success || match.Groups["indent"].Length != listIndent)
            {
                i++;
                continue;
            }

            var start = i;
            var endExclusive = FindEntryEnd(start, listIndent, limit);
            entries.Add(PatchEntry.Parse(this, start, endExclusive, listIndent));
            i = endExclusive;
        }

        return entries;
    }

    internal string LineAt(int index) => _lines[index];

    private int FindEntryEnd(int start, int listIndent, int limit)
    {
        for (var i = start + 1; i < limit; i++)
        {
            var line = _lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var match = DashLineRegex().Match(line);
            if (match.Success && match.Groups["indent"].Length <= listIndent)
                return i;
        }

        return limit;
    }

    /// <summary>校验文件仍然形如顶层 YAML 数组（管理功能使用的子集）。</summary>
    public bool ValidateStructure(out string error)
    {
        error = "";
        var nonComment = _lines.Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#')).ToList();
        if (nonComment.Count == 0)
        {
            error = "patch 文件不能为空（使用 [] 表示空层）";
            return false;
        }

        var first = nonComment[0];
        if (first.Trim() != "[]" && !DashLineRegex().IsMatch(first))
        {
            error = "顶层必须是 YAML 数组（'-' 条目或 []）";
            return false;
        }

        if (first.Trim() == "[]" && nonComment.Count > 1)
        {
            error = "[] 后不能再有内容";
            return false;
        }

        return true;
    }

    private string Render()
    {
        var text = string.Join("\n", _lines).TrimEnd('\n');
        return text.Length == 0 ? "[]\n" : text + "\n";
    }

    public void Save()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // 无 BOM UTF-8 + 临时文件 + 替换，符合 PLAN 第八节。
        var temp = Path + ".dshgui-tmp";
        File.WriteAllText(temp, Render(), new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
    }

    /// <summary>查找顶层 id 覆盖条目（不含 insert 条目）。</summary>
    public PatchEntry? FindTopLevelOverride(string id) =>
        TopLevelEntries.FirstOrDefault(e => !e.IsInsertList && e.Id == id);

    /// <summary>在任意 insert 列表中查找手工插入的行。</summary>
    public PatchEntry? FindInsertedRow(string id) =>
        TopLevelEntries
            .Where(e => e.IsInsertList)
            .SelectMany(e => e.InsertedRows)
            .FirstOrDefault(e => e.Id == id);

    /// <summary>设置或清除某个条目的 disabled 字段；返回修改后的同 id 新条目。</summary>
    public PatchEntry? SetDisabled(string id, bool? value)
    {
        var entry = FindTopLevelOverride(id) ?? FindInsertedRow(id);
        if (entry == null)
            return null;

        if (entry.DisabledLineIndex >= 0)
        {
            if (value == null)
            {
                _lines.RemoveAt(entry.DisabledLineIndex);
                RemoveLeadingCommentBlock(entry.DisabledLineIndex);
            }
            else
            {
                _lines[entry.DisabledLineIndex] =
                    new string(' ', entry.FieldIndent) + "disabled: " + BoolLiteral(value.Value);
            }
        }
        else if (value != null)
        {
            var insertAt = entry.IdLineIndex >= 0 ? entry.IdLineIndex + 1 : entry.DashLineIndex + 1;
            var indent = entry.FieldIndent;
            _lines.Insert(insertAt, new string(' ', indent) + "disabled: " + BoolLiteral(value.Value));
        }

        Rebuild();

        // 清除 disabled 后，若顶层覆盖只剩 id 一个字段，整个条目已无意义，直接删除。
        if (value == null)
        {
            var cleared = FindTopLevelOverride(id);
            if (cleared != null && IsIdOnlyOverride(cleared))
            {
                RemoveEntryRange(cleared.DashLineIndex, cleared.EndLineIndexExclusive);
                Rebuild();
                if (TopLevelEntries.Count == 0)
                    _lines = ["[]", ""];
            }
            return null;
        }

        return FindTopLevelOverride(id) ?? FindInsertedRow(id);
    }

    private bool IsIdOnlyOverride(PatchEntry entry)
    {
        if (entry.IsInsertList || entry.Name != null || entry.DisabledLineIndex >= 0)
            return false;

        for (var i = entry.DashLineIndex + 1; i < entry.EndLineIndexExclusive; i++)
        {
            var line = _lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;
            var match = Regex.Match(line, @"^(?<indent>[ \t]*)(?<key>[A-Za-z_][A-Za-z0-9_-]*)\s*:");
            if (!match.Success)
                continue;
            if (match.Groups["indent"].Length != entry.ListIndent + 2)
                continue;
            var key = match.Groups["key"].Value;
            if (key is "id" or "name" or "disabled" or "insert")
                continue;
            return false;
        }

        return true;
    }

    /// <summary>添加或更新顶层 id 覆盖（用于屏蔽 bundle 插入的行）。</summary>
    public PatchEntry AddOrUpdateTopLevelOverride(string id, bool? disabled)
    {
        var existing = FindTopLevelOverride(id);
        if (existing != null)
            return SetDisabled(id, disabled)!;

        // 文件只有 "[]" 时不能在其后追加，必须把 "[]" 替换成真实条目。
        if (TryReplaceEmptyArrayWithFirstEntry($"- id: {YamlScalar(id)}"))
        {
            Rebuild();
            var added = FindTopLevelOverride(id)!;
            _lines.Insert(added.DashLineIndex + 1,
                "  disabled: " + BoolLiteral(disabled ?? true));
            Rebuild();
            return FindTopLevelOverride(id)!;
        }

        if (_lines.Count > 0 && _lines[^1] != "")
            _lines.Add("");
        _lines.Add($"- id: {YamlScalar(id)}");
        _lines.Add("  disabled: " + BoolLiteral(disabled ?? true));
        Rebuild();
        return FindTopLevelOverride(id)!;
    }

    /// <summary>若文档是空数组 []，把 [] 替换为首个条目行；返回是否发生了替换。</summary>
    private bool TryReplaceEmptyArrayWithFirstEntry(string firstEntryLine)
    {
        var index = 0;
        for (; index < _lines.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(_lines[index]))
                continue;
            if (_lines[index].TrimStart().StartsWith('#'))
                continue;
            break;
        }

        if (index >= _lines.Count || _lines[index].Trim() != "[]")
            return false;

        _lines[index] = firstEntryLine;
        return true;
    }

    /// <summary>删除条目及其前导注释块；删除后重建索引。</summary>
    public void RemoveEntry(string id, bool fromInsertedRows)
    {
        var entry = fromInsertedRows ? FindInsertedRow(id) : FindTopLevelOverride(id);
        if (entry == null)
            return;

        RemoveEntryRange(entry.DashLineIndex, entry.EndLineIndexExclusive);
        Rebuild();

        // 删除的是 insert 内最后一行时，把空的 insert 外壳一并移除。
        foreach (var emptyInsert in TopLevelEntries
                     .Where(e => e.IsInsertList && e.InsertedRows.Count == 0)
                     .ToList())
        {
            RemoveEntryRange(emptyInsert.DashLineIndex, emptyInsert.EndLineIndexExclusive);
        }

        Rebuild();

        // 全部条目删完后恢复为空层，避免留下空文件或注释-only 文件。
        if (TopLevelEntries.Count == 0)
            _lines = ["[]", ""];
    }

    private void RemoveEntryRange(int dashLineIndex, int endLineIndexExclusive)
    {
        var start = dashLineIndex;
        while (start > 0 && IsCommentOnly(_lines[start - 1]))
            start--;

        var end = endLineIndexExclusive;
        while (end < _lines.Count && _lines[end] == "")
            end++;

        _lines.RemoveRange(start, end - start);
    }

    private void Rebuild() => TopLevelEntries = ScanList(0, 0, _lines.Count);

    private void RemoveLeadingCommentBlock(int fromIndex)
    {
        while (fromIndex > 0 && IsCommentOnly(_lines[fromIndex - 1]))
        {
            _lines.RemoveAt(fromIndex - 1);
            fromIndex--;
        }
    }

    private static bool IsCommentOnly(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith('#');
    }

    public static string YamlScalar(string value)
    {
        if (value.Length == 0)
            return "''";
        if (Regex.IsMatch(value, @"^[A-Za-z0-9_.@/-]+$") && !value.StartsWith('-'))
            return value;
        return "'" + value.Replace("'", "''") + "'";
    }

    public static string BoolLiteral(bool value) => value ? "true" : "false";

    /// <summary>解析 disabled 标量：true/false 字面量或表达式文本。</summary>
    public static (bool? Value, string Raw) ParseDisabled(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (null, "");
        var trimmed = raw.Trim();
        if (trimmed == "true")
            return (true, trimmed);
        if (trimmed == "false")
            return (false, trimmed);
        return (null, trimmed);
    }

    public static string? ParseScalar(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
            return null;
        if (trimmed.StartsWith("!!js", StringComparison.Ordinal))
            return trimmed;
        if (trimmed is "true" or "false" or "null" or "~")
            return trimmed is "true" ? "true" : trimmed is "false" ? "false" : null;
        if (trimmed[0] is '\'' or '"')
            return Unquote(trimmed);
        var hash = trimmed.IndexOf(" #", StringComparison.Ordinal);
        if (hash >= 0)
            trimmed = trimmed[..hash].TrimEnd();
        return trimmed;
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2)
            return value;
        var quote = value[0];
        if (value[^1] != quote)
            return value[1..];
        return value[1..^1]
            .Replace(quote == '\'' ? "''" : "\\\"", quote == '\'' ? "'" : "\"");
    }

    [GeneratedRegex(@"^(?<indent>[ \t]*)-(?<rest>.*)$")]
    private static partial Regex DashLineRegex();
}

/// <summary>patch 列表中的一个条目（顶层覆盖条目或 insert 列表）。</summary>
public sealed class PatchEntry
{
    public required PatchDocument Document { get; init; }

    /// <summary>条目 '-' 所在行号。</summary>
    public int DashLineIndex { get; init; }

    /// <summary>条目结束行号（不含）。</summary>
    public int EndLineIndexExclusive { get; init; }

    /// <summary>'-' 的缩进。</summary>
    public int ListIndent { get; init; }

    /// <summary>字段缩进（'-' 缩进 + 2；单行 '- id: x' 为 0）。</summary>
    public int FieldIndent { get; init; }

    public string? Id { get; init; }

    public string? Name { get; init; }

    public bool? Disabled { get; init; }

    public string DisabledRaw { get; init; } = "";

    public int IdLineIndex { get; init; } = -1;

    public int DisabledLineIndex { get; init; } = -1;

    public bool IsInsertList { get; init; }

    public List<PatchEntry> InsertedRows { get; init; } = [];

    public static PatchEntry Parse(PatchDocument document, int start, int endExclusive, int listIndent)
    {
        var line = document.LineAt(start);
        var match = Regex.Match(line, @"^(?<indent>[ \t]*)-(?<rest>.*)$");
        var firstRest = match.Groups["rest"].Value.Trim();

        string? id = null;
        string? name = null;
        bool? disabled = null;
        var disabledRaw = "";
        var idLine = -1;
        var disabledLine = -1;
        var fieldIndent = listIndent + 2;
        var isInsert = false;
        var insertKeyIndent = -1;
        var insertLine = -1;

        if (firstRest.Length > 0)
        {
            var inlineColon = firstRest.IndexOf(':');
            if (inlineColon > 0 && firstRest[..inlineColon].Trim() == "insert")
            {
                isInsert = true;
                insertKeyIndent = listIndent;
                insertLine = start;
            }
            else
            {
                fieldIndent = listIndent + 2;
                (id, name, disabled, disabledRaw, idLine, disabledLine) =
                    ParseInlineField(firstRest, id, name, disabled, disabledRaw, idLine, disabledLine);
            }
        }

        for (var i = start + 1; i < endExclusive; i++)
        {
            var current = document.LineAt(i);
            if (!TryMatchField(current, out var indent, out var key, out var value))
                continue;
            if (key == "insert" && indent == listIndent + 2)
            {
                isInsert = true;
                insertKeyIndent = indent;
                insertLine = i;
                continue;
            }
            if (isInsert && indent > listIndent + 2)
                continue; // insert 内的行归嵌套列表解析。
            if (indent != listIndent + 2)
                continue;

            fieldIndent = listIndent + 2;
            switch (key)
            {
                case "id":
                    id = PatchDocument.ParseScalar(value);
                    idLine = i;
                    break;
                case "name":
                    name = PatchDocument.ParseScalar(value);
                    break;
                case "disabled":
                    (disabled, disabledRaw) = PatchDocument.ParseDisabled(value);
                    disabledLine = i;
                    break;
            }
        }

        var rows = new List<PatchEntry>();
        if (isInsert)
        {
            var nestedIndent = FindNestedListIndent(document, insertLine + 1, endExclusive, insertKeyIndent);
            if (nestedIndent >= 0)
                rows = ScanRange(document, nestedIndent, insertLine + 1, endExclusive);
        }

        return new PatchEntry
        {
            Document = document,
            DashLineIndex = start,
            EndLineIndexExclusive = endExclusive,
            ListIndent = listIndent,
            FieldIndent = fieldIndent,
            Id = id,
            Name = name,
            Disabled = disabled,
            DisabledRaw = disabledRaw,
            IdLineIndex = idLine,
            DisabledLineIndex = disabledLine,
            IsInsertList = isInsert,
            InsertedRows = rows,
        };
    }

    private static (string? Id, string? Name, bool? Disabled, string Raw, int IdLine, int DisabledLine)
        ParseInlineField(
            string rest,
            string? id,
            string? name,
            bool? disabled,
            string disabledRaw,
            int idLine,
            int disabledLine)
    {
        var colon = rest.IndexOf(':');
        if (colon <= 0)
            return (id, name, disabled, disabledRaw, idLine, disabledLine);
        var key = rest[..colon].Trim();
        var value = rest[(colon + 1)..];
        switch (key)
        {
            case "id":
                id = PatchDocument.ParseScalar(value);
                idLine = -2; // 内联字段：无独立行，编辑时插入新行。
                break;
            case "name":
                name = PatchDocument.ParseScalar(value);
                break;
            case "disabled":
                (disabled, disabledRaw) = PatchDocument.ParseDisabled(value);
                disabledLine = -2;
                break;
        }

        return (id, name, disabled, disabledRaw, idLine, disabledLine);
    }

    private static int FindNestedListIndent(PatchDocument document, int from, int to, int insertIndent)
    {
        for (var i = from; i < to; i++)
        {
            var line = document.LineAt(i);
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (line.TrimStart().StartsWith('#'))
                continue;
            var match = Regex.Match(line, @"^(?<indent>[ \t]*)-(?<rest>.*)$");
            if (match.Success && match.Groups["indent"].Length > insertIndent)
                return match.Groups["indent"].Length;
            return -1;
        }

        return -1;
    }

    private static List<PatchEntry> ScanRange(PatchDocument document, int listIndent, int from, int to)
    {
        var entries = new List<PatchEntry>();
        var i = from;
        while (i < to)
        {
            var line = document.LineAt(i);
            var match = Regex.Match(line, @"^(?<indent>[ \t]*)-(?<rest>.*)$");
            if (!match.Success || match.Groups["indent"].Length != listIndent)
            {
                i++;
                continue;
            }

            var start = i;
            var end = FindEnd(document, start, listIndent, to);
            entries.Add(Parse(document, start, end, listIndent));
            i = end;
        }

        return entries;
    }

    private static int FindEnd(PatchDocument document, int start, int listIndent, int limit)
    {
        for (var i = start + 1; i < limit; i++)
        {
            var line = document.LineAt(i);
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var match = Regex.Match(line, @"^(?<indent>[ \t]*)-(?<rest>.*)$");
            if (match.Success && match.Groups["indent"].Length <= listIndent)
                return i;
        }

        return limit;
    }

    private static bool TryMatchField(string line, out int indent, out string key, out string value)
    {
        indent = -1;
        key = "";
        value = "";
        if (string.IsNullOrWhiteSpace(line))
            return false;
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#') || trimmed.StartsWith('-'))
            return false;
        var match = Regex.Match(line, @"^(?<indent>[ \t]*)(?<key>[A-Za-z_][A-Za-z0-9_-]*)\s*:(?<value>.*)$");
        if (!match.Success)
            return false;
        indent = match.Groups["indent"].Length;
        key = match.Groups["key"].Value;
        value = match.Groups["value"].Value;
        return true;
    }
}
