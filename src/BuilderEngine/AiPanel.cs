using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuilderEngine.Script;

namespace BuilderEngine;

/// <summary>What the AI panel needs from the editor (MainWindow.Ai.cs).</summary>
interface IAiHost
{
    AiContext BuildContext(AiMode mode);
    Task<IAiClient> CreateClientAsync();
    IEnumerable<string> Secrets { get; }
    string ProviderLabel { get; }
    /// <summary>The mechanism Edit / Explain work on (null = none open).</summary>
    string? OpenMechanismName { get; }
    /// <summary>The error Fix would send (null = nothing to fix).</summary>
    string? ErrorToFix { get; }
    string LogFile { get; }
    void Accept(AiResult result);
    void OpenSettings();
    void Log(string line);
}

/// <summary>
/// The AI panel (right column, "✨ AI" tab): Make / Edit / Explain / Fix, a short conversation, and the preview of what
/// the AI wrote (a diff for changes) with Accept / Discard. Nothing reaches the project without Accept.
/// </summary>
sealed class AiPanel : Border
{
    static readonly IBrush AddBg = new SolidColorBrush(Color.Parse("#16271C")).ToImmutable();
    static readonly IBrush AddFg = new SolidColorBrush(Color.Parse("#BFE8C9")).ToImmutable();
    static readonly IBrush DelBg = new SolidColorBrush(Color.Parse("#2E1512")).ToImmutable();
    static readonly IBrush DelFg = new SolidColorBrush(Color.Parse("#F2B8AE")).ToImmutable();
    static readonly IBrush UserBg = Palette.AccentSoft;

    readonly IAiHost _host;
    readonly StackPanel _chat = new() { Spacing = 10, Margin = new Thickness(12, 4, 12, 12) };
    readonly ScrollViewer _scroll;
    readonly TextBox _input = new();
    readonly Button _send = MainWindow.AccentButton("Send");
    readonly Button _cancel = MainWindow.PlainButton("Cancel");
    readonly TextBlock _busyText = new() { Foreground = Palette.TextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _provider = new() { Foreground = Palette.TextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _hint = new() { Foreground = Palette.TextDim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 8) };
    readonly Dictionary<AiMode, Button> _modeButtons = new();
    CancellationTokenSource? _cts;
    Border? _pendingCard;

    public AiMode Mode { get; private set; } = AiMode.Make;
    public bool Busy { get; private set; }
    /// <summary>The last result shown (dev snapshots read it).</summary>
    public AiResult? LastResult { get; private set; }

    public AiPanel(IAiHost host)
    {
        _host = host;
        Background = Palette.Bg1;

        // ---- Header: title, which AI, settings ----
        var title = new TextBlock { Text = "✨ AI assistant", FontWeight = FontWeight.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var gear = MainWindow.PlainButton("⚙");
        gear.Padding = new Thickness(9, 3);
        ToolTip.SetTip(gear, "AI settings: which AI answers (local or cloud), keys, language");
        gear.Click += (_, _) => _host.OpenSettings();
        var clear = MainWindow.PlainButton("Clear");
        clear.Padding = new Thickness(9, 3);
        clear.FontSize = 11;
        ToolTip.SetTip(clear, "Clear the conversation");
        clear.Click += (_, _) => { _chat.Children.Clear(); _pendingCard = null; Welcome(); };
        var headerRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        headerRight.Children.Add(clear);
        headerRight.Children.Add(gear);
        var header = new DockPanel { Margin = new Thickness(12, 10, 12, 2) };
        DockPanel.SetDock(headerRight, Dock.Right);
        header.Children.Add(headerRight);
        var titleCol = new StackPanel { Spacing = 1 };
        titleCol.Children.Add(title);
        titleCol.Children.Add(_provider);
        header.Children.Add(titleCol);

        // ---- Modes ----
        var modes = new UniformGrid { Columns = 4, Margin = new Thickness(12, 8, 12, 6) };
        foreach (var (mode, text, tip) in new[]
        {
            (AiMode.Make, "Make", "Make a new mechanism from a sentence"),
            (AiMode.Edit, "Edit", "Change the open mechanism (\"make the jump higher\")"),
            (AiMode.Explain, "Explain", "Explain the open mechanism, the selected lines, or how to do something — never changes anything"),
            (AiMode.Fix, "Fix", "Fix the error in the console or in the open mechanism"),
        })
        {
            var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, FontSize = 12, Margin = new Thickness(2, 0) };
            ToolTip.SetTip(b, tip);
            var m = mode;
            b.Click += (_, _) => SetMode(m);
            _modeButtons[mode] = b;
            modes.Children.Add(b);
        }

        // ---- Input ----
        _input.AcceptsReturn = true;
        _input.TextWrapping = TextWrapping.Wrap;
        _input.MinHeight = 58;
        _input.MaxHeight = 140;
        _input.FontSize = 13;
        _input.Background = Palette.Bg2;
        _input.Foreground = Palette.Text;
        _input.BorderBrush = Palette.Border;
        _input.CaretBrush = Palette.Accent;
        _input.SelectionBrush = Palette.AccentSoft;
        _input.Padding = new Thickness(8, 6);
        _input.Resources["TextControlBorderBrushFocused"] = Palette.Accent;
        _input.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { e.Handled = true; _ = SendAsync(_input.Text ?? ""); }
        }, RoutingStrategies.Tunnel);
        _send.Padding = new Thickness(16, 6);
        _send.Click += (_, _) => _ = SendAsync(_input.Text ?? "");
        _cancel.Padding = new Thickness(12, 6);
        _cancel.IsVisible = false;
        _cancel.Click += (_, _) => _cts?.Cancel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_send);
        var actionRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        actionRow.Children.Add(buttons);
        actionRow.Children.Add(_busyText);
        var inputBox = new StackPanel { Margin = new Thickness(12, 6, 12, 12) };
        inputBox.Children.Add(_input);
        inputBox.Children.Add(actionRow);
        var bottom = new Border { BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = inputBox };

        _scroll = new ScrollViewer { Content = _chat, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var top = new StackPanel();
        top.Children.Add(header);
        top.Children.Add(modes);
        top.Children.Add(_hint);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(_scroll);
        Child = root;

        SetMode(AiMode.Make);
        RefreshProvider();
        Welcome();
    }

    void Welcome() => AddNote("Ask in your own words. Make writes a new mechanism, Edit changes the open one, Explain teaches, Fix repairs an error. " +
                               "You always see a preview first — nothing changes until you press Accept.");

    public void RefreshProvider() => _provider.Text = _host.ProviderLabel;

    public void SetMode(AiMode mode)
    {
        Mode = mode;
        foreach (var (m, b) in _modeButtons)
        {
            bool on = m == mode;
            b.Background = on ? Palette.AccentSoft : Palette.Bg2;
            b.Foreground = on ? Palette.Text : Palette.TextDim;
            b.BorderBrush = on ? Palette.AccentBorder : Palette.Border;
            b.BorderThickness = new Thickness(1);
            b.CornerRadius = new CornerRadius(7);
            b.Padding = new Thickness(4, 5);
        }
        string? open = _host.OpenMechanismName;
        (_hint.Text, _input.Watermark) = mode switch
        {
            AiMode.Make => ("Describe a mechanism; the AI writes it and tests it in your scene.", "make me a mechanism that… (e.g. a coin that spins and gives 5 points)"),
            AiMode.Edit => (open != null ? $"Changes {open}. You'll see what changes before anything happens." : "Open a mechanism first (click one in the library).", "make the jump higher / add a double jump"),
            AiMode.Explain => (open != null ? $"Explains {open} (or the lines you selected in the code), or answers how-to questions." : "Ask how to do something, or open a mechanism to have it explained.", "how do I make an enemy follow the player?"),
            _ => (_host.ErrorToFix is { } err ? "Will fix: " + Short(err, 140) : "No error to fix right now. When the console shows one, press ✨ Fix.", "(optional) anything the AI should know"),
        };
    }

    public void FocusInput() => Dispatcher.UIThread.Post(() => _input.Focus(), DispatcherPriority.Input);

    static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---------------- Sending ----------------

    /// <summary>Sends a request in the current mode (also used by --snap with a fake AI).</summary>
    public async Task SendAsync(string text)
    {
        if (Busy) return;
        text = text.Trim();
        var mode = Mode;
        if (text.Length == 0 && (mode is AiMode.Make or AiMode.Edit)) { FocusInput(); return; }
        if (mode == AiMode.Fix && _host.ErrorToFix == null) { AddNote("There's no error to fix right now."); return; }
        if (mode == AiMode.Edit && _host.OpenMechanismName == null) { AddNote("Open a mechanism first: click one in the library on the left (or use Make for a new one)."); return; }

        AddUser(mode == AiMode.Fix ? "Fix: " + Short(_host.ErrorToFix ?? "", 200) + (text.Length > 0 ? "\n" + text : "")
              : mode == AiMode.Explain && text.Length == 0 ? "Explain " + (_host.OpenMechanismName ?? "this") : text);
        _input.Text = "";
        SetBusy(true, mode == AiMode.Explain ? "Thinking…" : "Writing and testing… (a local model can take a minute)");
        _cts = new CancellationTokenSource();
        try
        {
            var ctx = _host.BuildContext(mode);
            IAiClient client;
            try { client = await _host.CreateClientAsync(); }
            catch (AiException e) { AddError(e.Message); return; }
            var assistant = new AiAssistant(client, line => Dispatcher.UIThread.Post(() => _host.Log(line)), _host.Secrets) { LogFile = _host.LogFile };
            var result = await assistant.RunAsync(mode, text, ctx, _cts.Token);
            ShowResult(result);
        }
        finally
        {
            SetBusy(false, "");
            _cts?.Dispose();
            _cts = null;
        }
    }

    void SetBusy(bool busy, string text)
    {
        Busy = busy;
        _send.IsEnabled = !busy;
        _cancel.IsVisible = busy;
        _busyText.Text = text;
    }

    /// <summary>Shows what came back: an answer, an error, or a preview to accept.</summary>
    public void ShowResult(AiResult r)
    {
        LastResult = r;
        if (r.Error != null && !r.HasCode) { AddError(r.Error); return; }
        if (r.Mode == AiMode.Explain) { AddAnswer(r.Text); return; }
        if (r.Translated) AddNote("Understood as: " + r.Request);
        if (r.Text.Length > 0) AddAnswer(r.Text);
        AddPreview(r);
    }

    // ---------------- The conversation ----------------

    void Add(Control c)
    {
        _chat.Children.Add(c);
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    void AddUser(string text) => Add(new Border
    {
        Background = UserBg, BorderBrush = Palette.AccentBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10, 10, 2, 10),
        Padding = new Thickness(10, 7), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(28, 0, 0, 0),
        Child = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Palette.Text },
    });

    void AddNote(string text) => Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = Palette.TextDim, FontStyle = FontStyle.Italic });

    void AddError(string text) => Add(new Border
    {
        Background = DelBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7),
        Child = new SelectableTextBlock { Text = "⚠ " + text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = DelFg },
    });

    /// <summary>An answer: prose as text, ``` parts as code.</summary>
    void AddAnswer(string text)
    {
        var col = new StackPanel { Spacing = 6 };
        var parts = text.Replace("\r\n", "\n").Split("```");
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            if (i % 2 == 1)
            {
                int nl = p.IndexOf('\n');
                if (nl >= 0 && !p[..nl].Contains(' ') && p[..nl].Length < 15) p = p[(nl + 1)..]; // the language tag
                col.Children.Add(CodeBox(p.TrimEnd()));
            }
            else if (p.Trim().Length > 0)
                col.Children.Add(Prose(p.Trim()));
        }
        Add(new Border { Background = Palette.Bg2, CornerRadius = new CornerRadius(10, 10, 10, 2), Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 18, 0), Child = col });
    }

    /// <summary>Answer text: `code` in the code font, **bold** without its stars.</summary>
    static Control Prose(string text)
    {
        var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Palette.Text, LineHeight = 18 };
        var parts = text.Replace("**", "").Split('`');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            tb.Inlines!.Add(i % 2 == 1
                ? new Avalonia.Controls.Documents.Run(parts[i]) { FontFamily = Palette.Mono, FontSize = 11.5, Foreground = Palette.Accent }
                : new Avalonia.Controls.Documents.Run(parts[i]));
        }
        return tb;
    }

    static Control CodeBox(string code) => new Border
    {
        Background = Palette.Bg0, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6),
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new SelectableTextBlock { Text = code, FontFamily = Palette.Mono, FontSize = 11.5, Foreground = Palette.Text, Margin = new Thickness(0, 0, 0, 12) },
        },
    };

    // ---------------- Preview ----------------

    void AddPreview(AiResult r)
    {
        if (_pendingCard != null) Collapse(_pendingCard, "Replaced by a newer answer.");
        var col = new StackPanel { Spacing = 8 };
        foreach (var m in r.Mechanisms)
        {
            var head = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            head.Inlines!.Add(new Avalonia.Controls.Documents.Run(m.IsNew ? "New mechanism: " : "Changes to ") { Foreground = Palette.TextDim });
            head.Inlines.Add(new Avalonia.Controls.Documents.Run(m.Name) { Foreground = Palette.Accent });
            col.Children.Add(head);
            col.Children.Add(m.IsNew ? CodeBox(m.Source.TrimEnd()) : DiffBox(m.Diff()));
        }
        if (r.Retried && r.Problems.Count == 0)
            col.Children.Add(new TextBlock { Text = "The first answer had an error; the AI fixed it on its second try.", FontSize = 11, Foreground = Palette.TextDim, TextWrapping = TextWrapping.Wrap });
        if (r.Problems.Count == 0)
            col.Children.Add(new TextBlock { Text = "✓ Tested in your scene — no errors.", FontSize = 11.5, Foreground = Palette.Ok, TextWrapping = TextWrapping.Wrap });
        else
            foreach (var p in r.Problems)
                col.Children.Add(new TextBlock { Text = "⚠ " + p, FontSize = 11.5, Foreground = Palette.Error, TextWrapping = TextWrapping.Wrap });

        var accept = MainWindow.AccentButton(r.Problems.Count == 0 ? "Accept" : "Accept anyway");
        accept.Padding = new Thickness(16, 6);
        ToolTip.SetTip(accept, r.Mechanisms.Any(m => !m.IsNew) ? "Replace the mechanism with this version (you can still edit it after)" : "Add it to the project and open it");
        var discard = MainWindow.PlainButton("Discard");
        discard.Padding = new Thickness(12, 6);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(accept);
        row.Children.Add(discard);
        col.Children.Add(row);

        var card = new Border
        {
            Background = Palette.Bg1, BorderBrush = Palette.AccentBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 9), Child = col,
        };
        accept.Click += (_, _) =>
        {
            _host.Accept(r);
            Collapse(card, "✓ " + string.Join(", ", r.Mechanisms.Select(m => m.IsNew ? $"added {m.Name}" : $"updated {m.Name}")) + ". Keep editing it, or ask for more changes.");
        };
        discard.Click += (_, _) => Collapse(card, "Discarded — nothing was changed.");
        _pendingCard = card;
        Add(card);
    }

    /// <summary>Accept from code (dev snapshots): what the Accept button does.</summary>
    public bool AcceptPending()
    {
        if (_pendingCard == null || LastResult == null || !LastResult.HasCode) return false;
        _host.Accept(LastResult);
        Collapse(_pendingCard, "✓ Accepted.");
        return true;
    }

    /// <summary>Discard from code (dev snapshots): what the Discard button does.</summary>
    public void DiscardPending()
    {
        if (_pendingCard != null) Collapse(_pendingCard, "Discarded — nothing was changed.");
    }

    public bool HasPendingPreview => _pendingCard != null;

    void Collapse(Border card, string note)
    {
        if (_pendingCard == card) _pendingCard = null;
        card.Child = new TextBlock { Text = note, FontSize = 11.5, Foreground = Palette.TextDim, TextWrapping = TextWrapping.Wrap };
        card.BorderBrush = Palette.Border;
    }

    static Control DiffBox(List<AiDiffLine> lines)
    {
        var list = new StackPanel();
        foreach (var l in lines)
        {
            var (bg, fg, mark) = l.Kind switch
            {
                '+' => (AddBg, AddFg, "+ "),
                '-' => (DelBg, DelFg, "- "),
                _ => ((IBrush)Brushes.Transparent, Palette.TextDim, "  "),
            };
            list.Children.Add(new Border
            {
                Background = bg, Padding = new Thickness(4, 0),
                Child = new TextBlock { Text = mark + l.Text, FontFamily = Palette.Mono, FontSize = 11.5, Foreground = fg, TextDecorations = l.Kind == '-' ? TextDecorations.Strikethrough : null },
            });
        }
        return new Border
        {
            Background = Palette.Bg0, CornerRadius = new CornerRadius(6), Padding = new Thickness(0, 6), ClipToBounds = true,
            Child = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list },
        };
    }
}
