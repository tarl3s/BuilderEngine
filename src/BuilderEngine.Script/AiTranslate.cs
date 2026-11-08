using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuilderEngine.Script;

/// <summary>
/// A request written in another language (the owner speaks Portuguese) is first turned into English by the same AI in
/// one short JSON call (idea from EasyEngine, where it raised small local models from ~80% to ~90%): the examples are
/// picked by its words and small models follow an English request far better. Names, numbers and quoted text stay as
/// written; a translation that loses any of them isn't used. English requests skip the call.
/// </summary>
public static class AiTranslate
{
    public const string System =
        "Translate the user's request about a 2D game engine into plain English, as a command (\"make…\", \"add…\", \"when…\"). " +
        "Keep every name that starts with a capital letter (mechanisms, objects, scenes), every number and every text inside " +
        "quotes exactly as written. Translate the meaning with game words: inimigo/enemigo = enemy, chefe/jefe = boss, pulo/pular = jump, " +
        "moeda/moneda = coin, vida = health, dano = damage, espinho = spike, porta = door, tiro/atirar = shoot, tela/pantalla = screen, " +
        "\"vai e volta\" = \"moves back and forth\", \"pulo duplo\" = \"double jump\", \"mais alto\" = \"higher\". " +
        "Answer ONLY with JSON, e.g. for \"faz o Coin girar\": {\"english\":\"make the Coin spin\"}";

    static readonly Regex WordRx = new(@"\p{L}+'?\p{L}*", RegexOptions.Compiled);
    static readonly Regex QuotedRx = new("[\"“‘«']([^\"”’»']{2,})[\"”’»']", RegexOptions.Compiled);

    static readonly HashSet<string> English = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "an", "and", "or", "to", "of", "when", "make", "it", "is", "with", "every", "that", "on", "in", "at", "from",
        "after", "i", "me", "my", "create", "add", "give", "put", "let", "its", "into", "be", "while", "only", "if", "then", "each",
        "player", "enemy", "touches", "press", "show", "goes", "go", "seconds", "second", "dies", "him", "his", "her", "them", "up",
        "down", "can", "has", "have", "this", "for", "until", "but", "not", "don't", "build", "turn", "set", "remove", "delete", "also",
        "how", "what", "why", "does", "do", "explain", "jump", "higher", "faster", "slower", "mechanism", "fix", "error", "where",
    };

    static readonly HashSet<string> Foreign = new(StringComparer.OrdinalIgnoreCase)
    {
        // Portuguese
        "o", "os", "as", "um", "uma", "que", "de", "do", "da", "dos", "das", "para", "pra", "pro", "com", "quando", "faz", "faça", "cria",
        "crie", "coloca", "coloque", "deixa", "deixe", "ele", "ela", "no", "na", "em", "e", "é", "ao", "à", "se", "não", "mais", "jogador",
        "inimigo", "depois", "cada", "segundos", "tela", "vai", "fica", "eu", "aperto", "apertar", "encostar", "encosta", "pega", "tira",
        "como", "porque", "por", "isso", "esse", "essa", "este", "esta", "pulo", "pular", "mecanismo", "explica", "explique", "corrige",
        "conserta", "erro", "faço", "quero", "uns", "umas", "alto", "duplo", "moeda", "vida",
        // Spanish
        "el", "la", "los", "las", "y", "en", "con", "cuando", "haz", "pon", "jugador", "enemigo", "después", "pantalla", "del", "al",
        "lo", "su", "muy", "hacia", "presione", "toque", "crea", "salto", "saltar", "hago", "qué", "cómo",
        // French / German / Italian
        "le", "les", "et", "une", "des", "quand", "fais", "joueur", "ennemi", "après", "chaque", "écran", "avec", "il", "elle", "du",
        "der", "die", "das", "und", "mit", "wenn", "ein", "eine", "spieler", "gegner", "nach", "jede", "jeden", "sekunden", "mach",
        "gli", "giocatore", "nemico", "dopo", "ogni", "fai", "schermo",
    };

    /// <summary>True when the request is (mostly) English. Quoted text and names don't count; a non-Latin script is never English.</summary>
    public static bool LooksEnglish(string request)
    {
        var text = QuotedRx.Replace(request ?? "", " ");
        if (text.Any(c => c > 0x24F && char.IsLetter(c))) return false;
        int en = 0, other = 0;
        foreach (Match m in WordRx.Matches(text))
        {
            if (char.IsUpper(m.Value[0]) && m.Index > 0) continue; // a name ("Coin", "Level2")
            if (English.Contains(m.Value)) en++;
            if (Foreign.Contains(m.Value)) other++;
        }
        return other == 0 || en > other;
    }

    /// <summary>The model's translation if it can be trusted, else null: keeps every number, quoted text and name of the original.</summary>
    public static string? Clean(string original, string reply)
    {
        string s = (reply ?? "").Trim();
        int a = s.IndexOf('{'), b = s.LastIndexOf('}');
        if (a >= 0 && b > a)
            try
            {
                using var doc = JsonDocument.Parse(s[a..(b + 1)], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var root = doc.RootElement;
                s = root.ValueKind != JsonValueKind.Object ? ""
                    : (root.TryGetProperty("english", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()
                        : root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString()).FirstOrDefault()) ?? "";
            }
            catch (JsonException) { return null; }
        if (s.Contains("request in English", StringComparison.OrdinalIgnoreCase) || s == "make the Coin spin") return null; // the prompt copied back
        s = Regex.Replace(s, @"^(english|translation)\s*:\s*", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*\n\s*", " ").Trim();
        if (s.Length < 3 || s.Length > original.Length * 3 + 40) return null;
        foreach (Match m in Regex.Matches(original, @"\d+(?:[.,]\d+)?"))
            if (!s.Contains(m.Value) && !s.Contains(m.Value.Replace(',', '.'))) return null;
        foreach (Match m in QuotedRx.Matches(original)) if (!s.Contains(m.Groups[1].Value)) return null;
        foreach (Match m in Regex.Matches(original, @"(?<![.!?:;]\s*|^\s*)(?<=\s)\p{Lu}[\p{L}\d_]*"))
            if (!Regex.IsMatch(s, @"(?<![\p{L}\d])" + Regex.Escape(m.Value) + @"(?![\p{L}\d])")) return null;
        return s;
    }

    /// <summary>The request in English (unchanged when it already is, or when the translation can't be trusted).</summary>
    public static async Task<string> ToEnglishAsync(string request, IAiClient client, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request) || LooksEnglish(request)) return request;
        try { return Clean(request, await client.GenerateAsync(System, request, json: true, ct)) ?? request; }
        catch (Exception ex) when (ex is not OperationCanceledException and not AiException) { return request; }
    }
}
