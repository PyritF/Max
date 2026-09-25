using Spectre.Console;
using Spectre.Console.Rendering;

namespace Max.Ui;

/// <summary>
/// Der Startbildschirm nach dem Hochfahren: roter Kasten mit Begrüßung, Logo, Systeminfo,
/// Tipps und "Zuletzt".
/// Baut sich animiert auf: Logo zeilenweise, Begrüßung als Schreibmaschine, dann der Rest.
/// </summary>
internal static class HomeScreen
{
    // Das Logo in der Schrift "ANSI Shadow". Fest im Code statt als Schriftdatei,
    // damit Max aus einer einzigen Exe besteht und nichts nachladen muss.
    private const string Logo = """
        ███╗   ███╗ █████╗ ██╗  ██╗
        ████╗ ████║██╔══██╗╚██╗██╔╝
        ██╔████╔██║███████║ ╚███╔╝
        ██║╚██╔╝██║██╔══██║ ██╔██╗
        ██║ ╚═╝ ██║██║  ██║██╔╝ ██╗
        ╚═╝     ╚═╝╚═╝  ╚═╝╚═╝  ╚═╝
        """;

    private static readonly string[] LogoLines = PrepareLogo();

    private static readonly string[] TipPool =
    [
        "Frag einfach drauflos. Ich beiße nur selten.",
        "[/help] zeigt, was ich sonst noch kann.",
        "Strg+C bricht meine Antwort ab. Ich nehme es nicht persönlich.",
        "Ich laufe komplett auf diesem Rechner. Keine Cloud, keine Mithörer.",
        "Je genauer die Frage, desto brauchbarer die Antwort.",
    ];

    // Zeitplan des Aufbaus (ab Start der Übersicht).
    private static readonly TimeSpan LogoLineInterval = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan SystemInfoAt = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan GreetingAt = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan CharInterval = TimeSpan.FromMilliseconds(35);
    private static readonly TimeSpan TipsAt = TimeSpan.FromMilliseconds(1250);
    private static readonly TimeSpan LastSessionAt = TimeSpan.FromMilliseconds(1550);
    private static readonly TimeSpan EndAt = TimeSpan.FromMilliseconds(1850);
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(30);

    private const int LeftColumnWidth = 34;

    /// <summary>Was vom Kasten gerade schon sichtbar ist.</summary>
    private sealed record Reveal(int LogoLines, int GreetingChars, bool SystemInfo, bool Tips, bool LastSession)
    {
        public static Reveal All(int greetingLength) => new(int.MaxValue, greetingLength, true, true, true);

        public static Reveal At(TimeSpan t, int greetingLength) => new(
            LogoLines: (int)(t / LogoLineInterval) + 1,
            GreetingChars: t < GreetingAt ? 0 : Math.Min(greetingLength, (int)((t - GreetingAt) / CharInterval)),
            SystemInfo: t >= SystemInfoAt,
            Tips: t >= TipsAt,
            LastSession: t >= LastSessionAt);
    }

    public static async Task ShowAsync(SystemSnapshot system, CancellationToken ct = default)
    {
        var greeting = GetGreeting(system.Now, system.UserName);
        var tips = TipPool.OrderBy(_ => Random.Shared.Next()).Take(2).ToArray();
        var version = typeof(HomeScreen).Assembly.GetName().Version?.ToString(3) ?? "?";

        IRenderable Panel(Reveal r) => BuildPanel(r, greeting, tips, version, system);

        AnsiConsole.WriteLine();

        if (Console.IsOutputRedirected)
        {
            AnsiConsole.Write(Panel(Reveal.All(greeting.Length)));
        }
        else
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await AnsiConsole.Live(Panel(Reveal.At(TimeSpan.Zero, greeting.Length)))
                .AutoClear(false)
                .StartAsync(async ctx =>
                {
                    while (clock.Elapsed < EndAt)
                    {
                        ctx.UpdateTarget(Panel(Reveal.At(clock.Elapsed, greeting.Length)));
                        await Task.Delay(FrameInterval, ct);
                    }
                    ctx.UpdateTarget(Panel(Reveal.All(greeting.Length)));
                });
        }

        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Begrüßung nach Tageszeit, z. B. "Guten Abend, alex.".
    /// Später Fallback für die KI-Begrüßung (PLAN.md, 9a).
    /// </summary>
    public static string GetGreeting(DateTime now, string name) => now.Hour switch
    {
        >= 5 and < 12 => $"Guten Morgen, {name}.",
        >= 12 and < 18 => $"Guten Tag, {name}.",
        >= 18 and < 23 => $"Guten Abend, {name}.",
        _ => $"Noch wach, {name}?",
    };

    private static Panel BuildPanel(Reveal r, string greeting, string[] tips, string version, SystemSnapshot system)
    {
        var accent = Theme.Tag(Theme.Accent);
        var muted = Theme.Tag(Theme.Muted);
        var text = Theme.Tag(Theme.Text);

        // ── linke Spalte: Begrüßung, Logo, Systeminfo ──
        var typed = greeting[..r.GreetingChars];
        var typing = r.GreetingChars > 0 && r.GreetingChars < greeting.Length;
        var left = new List<IRenderable>
        {
            new Markup($"[bold {text}]{Markup.Escape(typed)}[/][{accent}]{(typing ? "▌" : "")}[/]"),
            Text.Empty,
        };

        for (var i = 0; i < LogoLines.Length; i++)
        {
            var color = Theme.Blend(Theme.GradientStart, Theme.GradientEnd, (float)i / (LogoLines.Length - 1));
            left.Add(i < r.LogoLines
                ? new Markup($"[bold {Theme.Tag(color)}]{Markup.Escape(LogoLines[i])}[/]")
                : new Markup(" "));
        }

        left.Add(Text.Empty);
        // Unsichtbare Zeilen als " " statt "": Leere Zeilen hätten keine Höhe und der Kasten würde springen.
        left.Add(new Markup(r.SystemInfo ? $"[{muted}]{Markup.Escape(system.Summary)}[/]" : " "));
        left.Add(new Markup(r.SystemInfo ? $"[{muted}]{Markup.Escape(Format.ShortenPath(system.WorkingDirectory, LeftColumnWidth))}[/]" : " "));

        // ── rechte Spalte: Tipps, Zuletzt ──
        var right = new List<IRenderable>();
        if (r.Tips)
        {
            right.Add(new Markup($"[bold {accent}]Tipps für den Anfang[/]"));
            // Eigene Spalte für "›", damit umgebrochene Tipps sauber eingerückt bleiben.
            var tipGrid = new Grid()
                .AddColumn(new GridColumn().Width(1).NoWrap().PadRight(1))
                .AddColumn(new GridColumn());
            foreach (var tip in tips)
                tipGrid.AddRow(new Markup($"[{muted}]›[/]"), new Markup($"[{text}]{Highlight(tip)}[/]"));
            right.Add(tipGrid);
        }
        if (r.LastSession)
        {
            right.Add(Text.Empty);
            right.Add(new Rule().RuleStyle(new Style(Theme.AccentDivider)));
            right.Add(Text.Empty);
            right.Add(new Markup($"[bold {accent}]Zuletzt[/]"));
            // TODO (Schritt 22, Gedächtnis): letzte Sitzung aus memory.json anzeigen.
            right.Add(new Markup($"[{muted}]Noch nichts. Wir fangen gerade erst an.[/]"));
        }

        // Tabelle ohne Kopfzeile mit "Minimal"-Rahmen: zeichnet nur die senkrechte Linie
        // zwischen den Spalten – immer so hoch wie die höhere Spalte.
        var columns = new Table()
            .Border(TableBorder.Minimal)
            .BorderColor(Theme.AccentDivider)
            .HideHeaders()
            .Expand()
            .AddColumn(new TableColumn("").Width(LeftColumnWidth).NoWrap().Centered().PadRight(2))
            .AddColumn(new TableColumn("").PadLeft(2));
        columns.AddRow(new Rows(left), new Rows(right));

        return new Panel(columns)
            .Header($"[{accent}] Max v{version} [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Theme.Accent)
            .Padding(1, 0) // die Tabelle bringt oben und unten schon je eine Leerzeile mit
            .Expand();
    }

    /// <summary>Hebt Befehle in eckigen Klammern hervor: "[/help] zeigt …" → "/help" in Akzentfarbe.</summary>
    private static string Highlight(string tip)
    {
        var escaped = Markup.Escape(tip); // "[/help]" wird zu "[[/help]]"
        return escaped.Replace("[[", $"[{Theme.Tag(Theme.Accent)}]").Replace("]]", "[/]");
    }

    private static string[] PrepareLogo()
    {
        // ReplaceLineEndings: Unter Windows kann die Quelldatei \r\n enthalten.
        var lines = Logo.ReplaceLineEndings("\n").Split('\n');
        // Alle Zeilen gleich breit, damit das zentrierte Logo nicht verrutscht.
        var width = lines.Max(l => l.Length);
        return lines.Select(l => l.PadRight(width)).ToArray();
    }
}
