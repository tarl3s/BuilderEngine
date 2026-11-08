using System.Runtime.CompilerServices;
using System.Text;

namespace BuilderEngine.Script;

/// <summary>
/// A record: a value with named fields, written <c>{name: "Ember Imp", cost: 1, attack: 2}</c>. Read and change a field
/// with a dot (<c>card.cost</c>, <c>card.health -= 2</c>) or by its name in brackets (<c>card["cost"]</c>); a missing
/// field reads as nothing. Like lists, a record is shared, not copied: <c>copy(card)</c> makes a new one.
/// Fields keep the order they were written in.
/// </summary>
public sealed class Record : Dictionary<string, object?>
{
    public Record() : base(StringComparer.Ordinal) { }

    internal static Record Make(RecordExpr e, Func<Expr, object?> eval)
    {
        var r = new Record();
        foreach (var (k, v) in e.Fields) r[k] = eval(v);
        return r;
    }

    /// <summary><c>{name: "Imp", cost: 1}</c> — field names that aren't plain words are written in quotes.</summary>
    internal static string Print(RecordExpr e)
    {
        if (e.Fields.Count == 0) return "{}";
        var sb = new StringBuilder("{");
        for (int i = 0; i < e.Fields.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Key(e.Fields[i].Key)).Append(": ").Append(Printer.Print(e.Fields[i].Value));
        }
        return sb.Append('}').ToString();
    }

    static string Key(string k) => IsWord(k) ? k : Printer.Quote(k);

    static bool IsWord(string k) =>
        k.Length > 0 && (char.IsLetter(k[0]) || k[0] == '_') && k.All(ch => char.IsLetterOrDigit(ch) || ch == '_') && !Parser.Keywords.Contains(k);

    public override string ToString()
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var (k, v) in this)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(Key(k)).Append(": ").Append(v is string s ? Printer.Quote(s) : World.ToText(v));
        }
        return sb.Append('}').ToString();
    }

    /// <summary>A new record with the same fields (lists and records inside are copied too).</summary>
    public Record Copy()
    {
        var r = new Record();
        foreach (var (k, v) in this) r[k] = World.CopyValue(v);
        return r;
    }
}

public sealed partial class Parser
{
    /// <summary>After '{': <c>name: value</c> pairs separated by commas (newlines inside are fine), then '}'.</summary>
    RecordExpr ParseRecord(int line)
    {
        var fields = new List<(string, Expr)>();
        while (!IsOp("}"))
        {
            if (Peek().Kind is Tok.Eof or Tok.Dedent or Tok.Newline) throw new ScriptError("A record '{' is missing its '}'.", line);
            if (IsOp(",")) { _p++; continue; }
            var k = Next();
            if (k.Kind is not (Tok.Ident or Tok.String))
                throw new ScriptError($"A record is written like {{name: \"Imp\", cost: 1}} — found {k} where a field name should be.", k.Line);
            if (!IsOp(":")) throw new ScriptError($"Write a ':' after the field name '{k.Text}', like {{{k.Text}: 1}}.", k.Line);
            _p++;
            if (IsOp(",") || IsOp("}")) throw new ScriptError($"The field '{k.Text}' needs a value after the ':'.", k.Line);
            if (fields.Any(f => f.Item1 == k.Text)) throw new ScriptError($"The record has two fields called '{k.Text}'.", k.Line);
            fields.Add((k.Text, ParseExpr()));
        }
        _p++;
        return new RecordExpr(fields, line);
    }
}

/// <summary>
/// Records and list helpers:
/// <code>
/// card = {name: "Ember Imp", cost: 1}     card.cost += 1     card["cost"]     has_key(card, "cost")     keys(card)
/// copy(card)        (a new record or list with the same values — changing it leaves the old one alone)
/// sorted(cards, "cost")     sorted(numbers)     reversed(list)     (new lists; the old one stays as it was)
/// </code>
/// </summary>
public sealed partial class World
{
    [ModuleInitializer]
    internal static void InstallRecords() => Installers.Add(w =>
    {
        w.Functions["keys"] = a => a.Count == 1 && a[0] is Record r ? r.Keys.Cast<object?>().ToList()
            : throw new ScriptError($"'keys' needs a record, like keys(card) — got {(a.Count == 1 ? Describe(a[0]) : a.Count + " values")}.", 0);
        w.Functions["has_key"] = a => a.Count == 2 ? a[0] is Record r && r.ContainsKey(ToText(a[1]))
            : throw new ScriptError("'has_key' needs a record and a field name, like has_key(card, \"cost\").", 0);
        w.Functions["copy"] = a => a.Count == 1 ? CopyValue(a[0]) : throw new ScriptError("'copy' needs one value, like copy(card).", 0);
        w.Functions["reversed"] = a =>
        {
            if (a.Count != 1 || a[0] is not List<object?> l) throw new ScriptError("'reversed' needs a list, like reversed(hand).", 0);
            var r = new List<object?>(l);
            r.Reverse();
            return r;
        };
        w.Functions["sorted"] = a =>
        {
            if (a.Count is < 1 or > 2 || a[0] is not List<object?> l)
                throw new ScriptError("'sorted' needs a list (and maybe a field to sort by), like sorted(cards, \"cost\").", 0);
            string? field = a.Count > 1 ? ToText(a[1]) : null;
            object? KeyOf(object? v) => field == null ? v : v switch
            {
                Record r => r.GetValueOrDefault(field),
                MechanismInstance m => m.Vars.GetValueOrDefault(field),
                GameObject g => field switch { "x" => g.X, "y" => g.Y, "width" => g.Width, "height" => g.Height, "name" => g.Name, _ => null },
                _ => null
            };
            var keyed = l.Select((v, i) => (V: v, K: KeyOf(v), I: i)).ToList();
            keyed.Sort((p, q) => { int c = CompareValues(p.K, q.K); return c != 0 ? c : p.I.CompareTo(q.I); }); // stable
            return keyed.Select(p => p.V).ToList();
        };
    });

    /// <summary>Numbers before texts, numbers by size, texts alphabetically (any case), nothing first.</summary>
    static int CompareValues(object? a, object? b)
    {
        if (a is null || b is null) return (a is null ? 0 : 1) - (b is null ? 0 : 1);
        bool an = a is double or bool, bn = b is double or bool;
        if (an && bn) return ToNumber(a, 0).CompareTo(ToNumber(b, 0));
        if (an != bn) return an ? -1 : 1;
        return string.Compare(ToText(a), ToText(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A copy of a list or record (and of the lists and records inside it); other values are themselves.</summary>
    internal static object? CopyValue(object? v) => v switch
    {
        Record r => r.Copy(),
        List<object?> l => l.Select(CopyValue).ToList(),
        _ => v
    };
}
