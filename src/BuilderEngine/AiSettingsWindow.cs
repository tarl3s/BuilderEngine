using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>
/// AI settings: which AI answers (Local / Ollama with its models, the free cloud tiers in turn, or one provider with the
/// person's own key), the keys, the language of explanations, and a "Test connection" button. Keys are typed in
/// password boxes and saved only in settings.json.
/// </summary>
sealed class AiSettingsWindow : Window
{
    readonly AiSettings _settings;
    readonly ComboBox _provider = Combo();
    readonly ComboBox _localModel = Combo();
    readonly ComboBox _answers = Combo();
    readonly TextBlock _localNote = Note("");
    readonly TextBlock _testResult = Note("");
    readonly Dictionary<string, TextBox> _keys = new();
    readonly TextBox _customUrl = Box("https://…/v1"), _customModel = Box("model name");
    readonly StackPanel _localSection = new() { Spacing = 6 }, _cloudSection = new() { Spacing = 6 }, _customSection = new() { Spacing = 6 };
    readonly List<(string Id, string Label)> _choices = [];

    /// <summary>True when the person pressed Save.</summary>
    public bool Saved { get; private set; }

    public AiSettingsWindow(AiSettings settings)
    {
        _settings = settings;
        Title = "AI settings";
        Width = 520; Height = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Palette.Bg1;
        Foreground = Palette.Text;

        _choices.Add((AiSettings.Local, "Local — Ollama on this computer (free, private)"));
        _choices.Add((AiSettings.Auto, "Cloud — free tiers, one after another (needs a free key)"));
        foreach (var p in CloudClient.Providers)
            _choices.Add((p.Id, p.Free ? $"Cloud — {p.Name} only (free key)" : $"Cloud — {p.Name} (your own paid key)"));
        _provider.ItemsSource = _choices.Select(c => c.Label).ToList();
        _provider.SelectedIndex = Math.Max(0, _choices.FindIndex(c => c.Id == settings.Provider));
        _provider.SelectionChanged += (_, _) => UpdateSections();

        // ---- Local ----
        var refresh = MainWindow.PlainButton("Refresh");
        refresh.Padding = new Thickness(10, 5);
        refresh.Click += (_, _) => _ = LoadModelsAsync();
        var start = MainWindow.PlainButton("Start Ollama");
        start.Padding = new Thickness(10, 5);
        ToolTip.SetTip(start, "Starts the Ollama server hidden (it must be installed: ollama.com)");
        start.Click += async (_, _) =>
        {
            _localNote.Text = "Starting Ollama…";
            var state = await OllamaManager.EnsureRunningAsync(_settings.OllamaUrl);
            _localNote.Text = state switch
            {
                OllamaManager.State.NotInstalled => "Ollama isn't installed. Get it at ollama.com, then run: ollama pull " + OllamaClient.SuggestedModel,
                OllamaManager.State.Failed => "Ollama didn't start. Open the Ollama app, then press Refresh.",
                _ => "Ollama is running.",
            };
            await LoadModelsAsync();
        };
        var modelRow = new DockPanel();
        var modelButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6, 0, 0, 0) };
        modelButtons.Children.Add(refresh);
        modelButtons.Children.Add(start);
        DockPanel.SetDock(modelButtons, Dock.Right);
        modelRow.Children.Add(modelButtons);
        modelRow.Children.Add(_localModel);
        // One-click setup: install Ollama and/or download a model (AiOllamaSetup.cs); the website if that fails.
        _setup.Click += async (_, _) => await RunSetupAsync();
        _website.Click += (_, _) => OpenWebsite();
        _website.IsVisible = false;
        var setupRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        setupRow.Children.Add(_setup);
        setupRow.Children.Add(_website);
        _setupPanel.Children.Add(setupRow);
        _setupPanel.Children.Add(_setupBar);
        _localSection.Children.Add(_setupPanel);
        _localSection.Children.Add(Label("Model"));
        _localSection.Children.Add(modelRow);
        _localSection.Children.Add(_localNote);
        _localSection.Children.Add(Note($"Small models run on most PCs: {OllamaSetup.StarterModel} (fast) or {OllamaClient.SuggestedModel} (a bit smarter, needs more memory)."));

        // ---- Cloud keys ----
        _cloudSection.Children.Add(Label("Keys (saved only on this computer, in settings.json)"));
        foreach (var p in CloudClient.Providers.Where(p => p.Id != "custom"))
        {
            var box = Box(p.Free ? "free key" : "your key");
            box.PasswordChar = '•';
            box.Text = settings.Key(p.Id);
            _keys[p.Id] = box;
            var name = new TextBlock { Text = p.Name, Width = 130, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
            var link = new TextBlock { Text = p.KeyUrl, FontSize = 10.5, Foreground = Palette.TextDim, Margin = new Thickness(130, 0, 0, 2) };
            var row = new DockPanel();
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);
            row.Children.Add(box);
            _cloudSection.Children.Add(row);
            _cloudSection.Children.Add(link);
        }

        // ---- Custom endpoint ----
        _customUrl.Text = settings.CustomUrl;
        _customModel.Text = settings.CustomModel;
        var customKey = Box("key (if it needs one)");
        customKey.PasswordChar = '•';
        customKey.Text = settings.Key("custom");
        _keys["custom"] = customKey;
        _customSection.Children.Add(Label("Custom OpenAI-compatible endpoint"));
        _customSection.Children.Add(_customUrl);
        _customSection.Children.Add(_customModel);
        _customSection.Children.Add(customKey);

        // ---- Answers ----
        _answers.ItemsSource = new List<string> { "Explanations in the language of my question", "Explanations always in English" };
        _answers.SelectedIndex = settings.AnswerLanguage == AiSettings.AnswerEnglish ? 1 : 0;

        // ---- Buttons ----
        var test = MainWindow.PlainButton("Test connection");
        test.Click += async (_, _) => await TestAsync();
        var save = MainWindow.AccentButton("Save");
        save.Click += (_, _) => { ApplyTo(_settings); Saved = true; Close(true); };
        var cancel = MainWindow.PlainButton("Cancel");
        cancel.Click += (_, _) => Close(false);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(cancel);
        right.Children.Add(save);
        var bar = new DockPanel { Margin = new Thickness(22, 10, 22, 16) };
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        test.HorizontalAlignment = HorizontalAlignment.Left;
        bar.Children.Add(test);

        var body = new StackPanel { Spacing = 12, Margin = new Thickness(22, 18, 22, 8) };
        body.Children.Add(new TextBlock { Text = "AI settings", FontSize = 15, FontWeight = FontWeight.SemiBold });
        body.Children.Add(Note("The AI writes mechanisms in Builder Script and explains them. It never changes your project until you press Accept."));
        body.Children.Add(Label("Which AI answers"));
        body.Children.Add(_provider);
        body.Children.Add(_localSection);
        body.Children.Add(_customSection);
        body.Children.Add(_cloudSection);
        body.Children.Add(Label("Answers"));
        body.Children.Add(_answers);
        body.Children.Add(Note("The editor speaks English; requests in Portuguese (or other languages) are understood — they are turned into English first for the code."));
        body.Children.Add(_testResult);
        if (!settings.CanSave)
            body.Children.Add(new TextBlock { Text = "This session can't write the settings file (opened with --project): changes last until you close BuilderEngine.", Foreground = Palette.Error, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });

        var root = new DockPanel();
        var bottom = MainWindow.WithBorder(bar, new Thickness(0, 1, 0, 0));
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(false); } };

        UpdateSections();
        ShowModels([]);
        _localNote.Text = "Looking for Ollama…";
        _ = LoadModelsAsync();
    }

    string SelectedProvider => _provider.SelectedIndex >= 0 && _provider.SelectedIndex < _choices.Count ? _choices[_provider.SelectedIndex].Id : AiSettings.Local;

    void UpdateSections()
    {
        string id = SelectedProvider;
        _localSection.IsVisible = id == AiSettings.Local;
        _customSection.IsVisible = id == "custom";
        _cloudSection.IsVisible = id != AiSettings.Local && id != "custom";
    }

    async Task LoadModelsAsync()
    {
        // Installed but not running: start it hidden first, so the list is real.
        if (OllamaSetup.IsInstalled) await OllamaManager.EnsureRunningAsync(_settings.OllamaUrl);
        var models = await new OllamaClient("", _settings.OllamaUrl).ListModelsAsync();
        ShowModels(models);
        _localNote.Text = models.Count > 0 ? $"{models.Count} model(s) installed."
            : OllamaSetup.IsInstalled ? "Ollama is installed but has no model yet — press “Download the AI model”."
            : "Ollama (the free program that runs the AI on this computer) isn't installed yet — press “Install Ollama”.";
        UpdateSetupButton(models.Count);
    }

    // ---------------- One-click setup ----------------

    readonly Button _setup = MainWindow.AccentButton("⬇  Install Ollama");
    readonly Button _website = MainWindow.PlainButton("Open ollama.com");
    readonly ProgressBar _setupBar = new() { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false, Foreground = Palette.Accent, Background = Palette.Bg2 };
    readonly StackPanel _setupPanel = new() { Spacing = 6 };
    bool _settingUp;

    void UpdateSetupButton(int modelCount)
    {
        if (_settingUp) return;
        bool installed = OllamaSetup.IsInstalled;
        _setupPanel.IsVisible = !installed || modelCount == 0;
        _setup.Content = installed ? "⬇  Download the AI model (about 1 GB)" : "⬇  Install Ollama + the AI model (about 2 GB, free)";
        ToolTip.SetTip(_setup, installed
            ? $"Downloads {OllamaSetup.StarterModel} through Ollama and selects it."
            : $"Downloads Ollama's official installer from ollama.com, installs it (no questions, no admin), starts it and downloads {OllamaSetup.StarterModel}.");
    }

    /// <summary>Install Ollama when missing, start it, download the starter model, select it and save.</summary>
    async Task RunSetupAsync()
    {
        if (_settingUp) return;
        _settingUp = true;
        _localNote.Foreground = Palette.TextDim;
        _setup.IsEnabled = false;
        _website.IsVisible = false;
        _setupBar.IsVisible = true;
        _setupBar.IsIndeterminate = false;
        try
        {
            if (!OllamaSetup.IsInstalled)
            {
                _localNote.Text = "Downloading Ollama…";
                string installer = await OllamaSetup.DownloadInstallerAsync(new Progress<double>(p =>
                {
                    _setupBar.Value = p;
                    _localNote.Text = $"Downloading Ollama… {Math.Round(p * 100)}%";
                }));
                _localNote.Text = "Installing Ollama… (this takes a minute; Windows may ask once)";
                _setupBar.IsIndeterminate = true;
                if (!await OllamaSetup.InstallAsync(installer))
                    throw new InvalidOperationException("The installer finished but Ollama wasn't found.");
            }

            _setupBar.IsIndeterminate = true;
            _localNote.Text = "Starting Ollama…";
            var state = await OllamaManager.EnsureRunningAsync(_settings.OllamaUrl);
            if (state is OllamaManager.State.Failed or OllamaManager.State.NotInstalled)
                throw new InvalidOperationException("Ollama is installed but didn't start.");

            var models = await new OllamaClient("", _settings.OllamaUrl).ListModelsAsync();
            string model = models.Contains(OllamaSetup.StarterModel) ? OllamaSetup.StarterModel
                : AiSettings.BestLocalModel(models) ?? OllamaSetup.StarterModel;
            if (!models.Contains(model))
            {
                _localNote.Text = $"Downloading the AI model {model}…";
                bool ok = await OllamaSetup.PullAsync(model, new Progress<OllamaSetup.PullProgress>(p =>
                {
                    if (p.Fraction >= 0) { _setupBar.IsIndeterminate = false; _setupBar.Value = p.Fraction; }
                    _localNote.Text = p.Fraction >= 0 ? $"Downloading the AI model {model}… {Math.Round(p.Fraction * 100)}%" : $"AI model: {p.Status}…";
                }), _settings.OllamaUrl);
                if (!ok) throw new InvalidOperationException($"Ollama couldn't download {model}.");
            }

            // Ready: pick it and keep it.
            _provider.SelectedIndex = Math.Max(0, _choices.FindIndex(c => c.Id == AiSettings.Local));
            _settings.Provider = AiSettings.Local;
            _settings.LocalModel = model;
            _settings.Save();
            _settingUp = false;
            await LoadModelsAsync();
            _localModel.SelectedItem = model;
            _localNote.Foreground = Palette.Ok;
            _localNote.Text = $"✓ Ready: the AI runs on this computer with {model}. Saved.";
        }
        catch (Exception e)
        {
            _localNote.Foreground = Palette.Error;
            _localNote.Text = $"⚠ {e.Message} You can install Ollama from its website instead; BuilderEngine sets it up by itself the next time it opens.";
            _website.IsVisible = true;
        }
        finally
        {
            _settingUp = false;
            _setup.IsEnabled = true;
            _setupBar.IsVisible = false;
        }
    }

    static void OpenWebsite()
    {
        try { Process.Start(new ProcessStartInfo { FileName = OllamaSetup.Website, UseShellExecute = true }); } catch { }
    }

    /// <summary>The model list: "automatic" first, then what Ollama has; keeps what the person picked.</summary>
    void ShowModels(List<string> models)
    {
        string want = _localModel.SelectedIndex > 0 && _localModel.SelectedItem is string picked ? picked : _settings.LocalModel;
        var items = new List<string> { "(automatic: the best one installed)" };
        items.AddRange(models);
        if (want.Length > 0 && !items.Contains(want)) items.Add(want);
        _localModel.ItemsSource = items;
        _localModel.SelectedIndex = want.Length > 0 ? items.IndexOf(want) : 0;
    }

    /// <summary>The window's values into a settings object (not yet written to disk).</summary>
    void ApplyTo(AiSettings s)
    {
        s.Provider = SelectedProvider;
        s.LocalModel = _localModel.SelectedIndex > 0 && _localModel.SelectedItem is string m ? m : "";
        s.CustomUrl = (_customUrl.Text ?? "").Trim();
        s.CustomModel = (_customModel.Text ?? "").Trim();
        s.AnswerLanguage = _answers.SelectedIndex == 1 ? AiSettings.AnswerEnglish : AiSettings.AnswerSameAsQuestion;
        foreach (var (id, box) in _keys) s.SetKey(id, box.Text);
    }

    /// <summary>Asks the AI as typed now (nothing is saved).</summary>
    async Task TestAsync()
    {
        var copy = new AiSettings { OllamaUrl = _settings.OllamaUrl };
        ApplyTo(copy);

        _testResult.Foreground = Palette.TextDim;
        _testResult.Text = "Testing…";
        var clock = Stopwatch.StartNew();
        try
        {
            var models = copy.Provider == AiSettings.Local ? await new OllamaClient("", copy.OllamaUrl).ListModelsAsync() : [];
            var client = copy.CreateClient(models);
            string reply = await client.GenerateAsync("Answer with one short sentence.", "Say hello to a game maker in five words.");
            _testResult.Foreground = Palette.Ok;
            _testResult.Text = $"✓ {client.Name} answered in {AiAssistant.Seconds(clock)} s: “{Trim(reply, 80)}”";
        }
        catch (Exception e) when (e is AiException or HttpRequestException or TaskCanceledException)
        {
            _testResult.Foreground = Palette.Error;
            _testResult.Text = "⚠ " + AiSecrets.Redact(e.Message, copy.Secrets());
        }
    }

    static string Trim(string s, int max) { s = s.Replace('\n', ' ').Trim(); return s.Length <= max ? s : s[..max] + "…"; }

    static ComboBox Combo() => new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border,
    };

    static TextBox Box(string watermark)
    {
        var b = new TextBox
        {
            Watermark = watermark, Background = Palette.Bg2, Foreground = Palette.Text, BorderBrush = Palette.Border,
            FontSize = 12.5, Padding = new Thickness(8, 5), CaretBrush = Palette.Accent,
        };
        b.Resources["TextControlBorderBrushFocused"] = Palette.Accent;
        return b;
    }

    static TextBlock Label(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Palette.TextDim };
    static TextBlock Note(string text) => new() { Text = text, FontSize = 11.5, Foreground = Palette.TextDim, TextWrapping = TextWrapping.Wrap };
}
