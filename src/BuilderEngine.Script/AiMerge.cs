using System.Text.RegularExpressions;

namespace BuilderEngine.Script;

/// <summary>One mechanism the AI proposes: new (no <see cref="OldSource"/>) or a change to an existing one.</summary>
public sealed record AiMechanism(string Name, string Source, string? OldSource)
{
    public bool IsNew => OldSource == null;
    public List<AiDiffLine> Diff() => AiDiff.Lines(OldSource ?? "", Source);
}

/// <summary>A line of a preview: ' ' same, '+' added, '-' removed.</summary>
public readonly record struct AiDiffLine(char Kind, string Text);

/// <summary>Splitting files into mechanisms, renaming, and putting accepted mechanisms into the project's sources.</summary>
public static class AiMerge
{
    static readonly Regex Head = new(@"^mechanism[ \t]+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>A file with several mechanisms → one source per mechanism (text before the first one is dropped).</summary>
    public static List<(string Name, string Source)> Split(string source)
    {
        var lines = (source ?? "").Replace("\r\n", "\n").Split('\n');
        var starts = new List<int>();
        for (int i = 0; i < lines.Length; i++)
            if (Head.IsMatch(lines[i])) starts.Add(i);
        var list = new List<(string, string)>();
        for (int k = 0; k < starts.Count; k++)
        {
            int end = k + 1 < starts.Count ? starts[k + 1] : lines.Length;
            string body = string.Join("\n", lines[starts[k]..end]).TrimEnd() + "\n";
            list.Add((Head.Match(lines[starts[k]]).Groups[1].Value, body));
        }
        return list;
    }

    /// <summary>The source with its (first) mechanism renamed.</summary>
    public static string Rename(string source, string newName) =>
        Head.Replace(source, m => m.Value[..(m.Groups[1].Index - m.Index)] + newName, 1);

    /// <summary>A name not used yet: Coin → Coin2, Coin3...</summary>
    public static string UniqueName(string name, Func<string, bool> taken)
    {
        if (!taken(name)) return name;
        string stem = Regex.Replace(name, @"\d+$", "");
        for (int n = 2; ; n++)
            if (!taken(stem + n)) return stem + n;
    }

    /// <summary>
    /// The mechanisms of an answer, ready to preview. Edit / Fix: the first is the open mechanism (its new version).
    /// Make: a name that is already taken gets a number (nothing is overwritten by surprise). A mechanism identical
    /// to the one already there is left out.
    /// </summary>
    public static List<AiMechanism> Propose(string script, AiMode mode, AiContext ctx)
    {
        var result = new List<AiMechanism>();
        var parts = Split(script);
        for (int i = 0; i < parts.Count; i++)
        {
            var (name, src) = parts[i];
            if (i == 0 && (mode is AiMode.Edit or AiMode.Fix) && ctx.OpenName != null)
            {
                if (!string.Equals(name, ctx.OpenName, StringComparison.Ordinal)) src = Rename(src, name = ctx.OpenName);
                result.Add(new AiMechanism(name, src, ctx.OpenSource));
                continue;
            }
            if (ctx.Sources.TryGetValue(name, out var have))
            {
                if (Same(have, src)) continue;
                string unique = UniqueName(name, n => ctx.Sources.ContainsKey(n) || result.Any(r => r.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
                src = Rename(src, name = unique);
            }
            result.Add(new AiMechanism(name, src, null));
        }
        return result;
    }

    static bool Same(string a, string b) => a.Replace("\r\n", "\n").Trim() == b.Replace("\r\n", "\n").Trim();

    /// <summary>Accept: the proposed mechanisms go into the sources (new ones added, changed ones replaced). Returns the first name.</summary>
    public static string? Apply(IDictionary<string, string> sources, IEnumerable<AiMechanism> mechanisms)
    {
        string? first = null;
        foreach (var m in mechanisms)
        {
            sources[m.Name] = m.Source;
            first ??= m.Name;
        }
        return first;
    }
}

/// <summary>A line diff (longest common subsequence) for the preview: small mechanisms, so the simple way is fine.</summary>
public static class AiDiff
{
    public static List<AiDiffLine> Lines(string oldText, string newText)
    {
        var a = Split(oldText);
        var b = Split(newText);
        int n = a.Length, m = b.Length;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var result = new List<AiDiffLine>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { result.Add(new(' ', a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) result.Add(new('-', a[x++]));
            else result.Add(new('+', b[y++]));
        }
        while (x < n) result.Add(new('-', a[x++]));
        while (y < m) result.Add(new('+', b[y++]));
        return result;
    }

    static string[] Split(string s)
    {
        s = (s ?? "").Replace("\r\n", "\n").TrimEnd('\n');
        return s.Length == 0 ? [] : s.Split('\n').Select(l => l.TrimEnd()).ToArray();
    }
}
