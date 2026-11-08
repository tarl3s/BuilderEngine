namespace BuilderEngine.Script;

/// <summary>
/// Which AI the assistant uses and the person's keys, kept in the editor's <c>settings.json</c> (normally
/// <c>%APPDATA%\BuilderEngine</c>; <c>--appdata</c> moves it). Keys are stored there and nowhere else: never in a project,
/// a log or the console. A read-only <see cref="AppSettings"/> keeps changes for this session only.
/// </summary>
public sealed class AiSettings
{
    public const string Local = "local";
    public const string Auto = "auto";
    /// <summary>Explanations in the language of the question (the default) or always in English.</summary>
    public const string AnswerSameAsQuestion = "question", AnswerEnglish = "english";

    readonly AppSettings? _store;

    /// <summary>"local" (Ollama), "auto" (free cloud tiers) or a cloud provider id ("groq", "anthropic", "custom"...).</summary>
    public string Provider { get; set; } = Local;
    public string LocalModel { get; set; } = "";
    public string OllamaUrl { get; set; } = OllamaClient.DefaultUrl;
    public string CustomUrl { get; set; } = "";
    public string CustomModel { get; set; } = "";
    public string AnswerLanguage { get; set; } = AnswerSameAsQuestion;
    public Dictionary<string, string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public AiSettings() { }

    public AiSettings(AppSettings store)
    {
        _store = store;
        Provider = store.Get("aiProvider") is { Length: > 0 } p ? p : Local;
        LocalModel = store.Get("aiLocalModel") ?? "";
        OllamaUrl = store.Get("aiOllamaUrl") is { Length: > 0 } u ? u : OllamaClient.DefaultUrl;
        CustomUrl = store.Get("aiCustomUrl") ?? "";
        CustomModel = store.Get("aiCustomModel") ?? "";
        AnswerLanguage = store.Get("aiAnswerLanguage") is { Length: > 0 } a ? a : AnswerSameAsQuestion;
        foreach (var cp in CloudClient.Providers)
            if (store.Get("aiKey." + cp.Id) is { Length: > 0 } k) Keys[cp.Id] = k;
    }

    public bool CanSave => _store is { ReadOnly: false };

    /// <summary>Writes the settings (false when the settings file is read-only in this session or can't be written).</summary>
    public bool Save()
    {
        if (_store == null || _store.ReadOnly) return false;
        bool ok = _store.Set("aiProvider", Provider) & _store.Set("aiLocalModel", LocalModel) & _store.Set("aiOllamaUrl", OllamaUrl)
            & _store.Set("aiCustomUrl", CustomUrl) & _store.Set("aiCustomModel", CustomModel) & _store.Set("aiAnswerLanguage", AnswerLanguage);
        foreach (var cp in CloudClient.Providers) ok &= _store.Set("aiKey." + cp.Id, Key(cp.Id));
        return ok;
    }

    public string Key(string providerId) => Keys.TryGetValue(providerId, out var k) ? k : "";
    public void SetKey(string providerId, string? key) => Keys[providerId] = (key ?? "").Trim();

    /// <summary>Every key, so logs and messages can hide them (<see cref="AiSecrets.Redact"/>).</summary>
    public IEnumerable<string> Secrets() => Keys.Values.Where(k => !string.IsNullOrWhiteSpace(k));

    /// <summary>The client for the chosen AI. <paramref name="localModels"/>: what Ollama has, to pick one when none is set.</summary>
    public IAiClient CreateClient(IReadOnlyList<string>? localModels = null)
    {
        if (Provider == Local)
        {
            string model = LocalModel.Trim();
            if (model.Length == 0) model = BestLocalModel(localModels ?? []) ?? OllamaClient.SuggestedModel;
            return new OllamaClient(model, OllamaUrl);
        }
        if (Provider == Auto)
            return new CloudAuto(CloudClient.Providers.Where(p => p.Free && Key(p.Id).Length > 0).Select(p => (IAiClient)new CloudClient(p, Key(p.Id))).ToList());
        var cp = CloudClient.Provider(Provider) ?? throw new AiException($"Unknown AI '{Provider}'. Pick one in the AI settings.");
        return new CloudClient(cp, Key(cp.Id), CustomUrl, CustomModel);
    }

    /// <summary>The model to use when none was picked: a coder model, the bigger the better among the small ones.</summary>
    public static string? BestLocalModel(IReadOnlyList<string> models)
    {
        if (models.Count == 0) return null;
        static double Size(string m)
        {
            var x = System.Text.RegularExpressions.Regex.Match(m, @"(\d+(?:\.\d+)?)b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return x.Success ? double.Parse(x.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 7;
        }
        return models.Where(m => !m.Contains("embed", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Contains("coder", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => Size(m) <= 8 ? Size(m) : -Size(m)) // fits a gaming PC: up to ~8b first
            .FirstOrDefault();
    }

    /// <summary>A short label for the panel ("Local · qwen2.5-coder:3b", "Cloud · free tiers").</summary>
    public string Label() => Provider switch
    {
        Local => "Local · " + (LocalModel.Length > 0 ? LocalModel : "Ollama"),
        Auto => "Cloud · free tiers",
        "custom" => "Cloud · " + (CustomModel.Length > 0 ? CustomModel : "custom"),
        _ => "Cloud · " + (CloudClient.Provider(Provider)?.Name ?? Provider),
    };
}
