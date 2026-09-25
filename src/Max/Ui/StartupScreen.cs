using Max.Setup;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Max.Ui;

/// <summary>
/// Die Startsequenz: Schritte laufen nacheinander, jeder mit Spinner und am Ende einem Häkchen.
/// Ein Schritt, der Fortschritt meldet (Download), bekommt zusätzlich einen Fortschrittsbalken.
/// Scheitert ein Schritt (<see cref="SetupException"/> oder Abbruch mit Strg+C), steht ein ✗ mit
/// der Meldung da, und die Ausnahme wird danach weitergereicht.
/// </summary>
internal static class StartupScreen
{
    private const int MaxLabelWidth = 44;
    private const int DetailWidth = 34;
    private const int SidePadding = 2;
    private const int BarWidth = 40;

    // Jeder Schritt bleibt mindestens so lange sichtbar – sonst blitzt er nur auf.
    private static readonly TimeSpan MinStepDuration = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan PauseBetweenSteps = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan PauseAtEnd = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(80);

    private enum StepStatus { Pending, Running, Done, Failed }

    private sealed class StepState(StartupStep step)
    {
        public StartupStep Step { get; } = step;
        public StepStatus Status { get; set; } = StepStatus.Pending;
        public string Detail { get; set; } = "";
        public string? Error { get; set; }
        public StepProgress Progress { get; } = new();
    }

    public static async Task RunAsync(string title, string? subtitle, IReadOnlyList<StartupStep> steps, CancellationToken ct = default)
    {
        var states = steps.Select(s => new StepState(s)).ToList();
        var frames = AnsiConsole.Profile.Capabilities.Unicode ? Spinner.Known.Dots.Frames : Spinner.Known.Ascii.Frames;
        var frame = 0;

        IRenderable Render() => BuildView(title, subtitle, states, frames[frame % frames.Count]);

        Exception? failure = null;

        // Ohne echtes Terminal (z. B. Ausgabe in eine Datei): Schritte ohne Animation ausführen.
        if (Console.IsOutputRedirected)
        {
            foreach (var state in states)
            {
                failure = await CompleteAsync(state, state.Step.Run(state.Progress, ct));
                if (failure is not null)
                    break;
            }
            AnsiConsole.Write(Render());
        }
        else
        {
            await RunLiveAsync();
        }

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);

        async Task RunLiveAsync() => await AnsiConsole.Live(Render())
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                foreach (var state in states)
                {
                    state.Status = StepStatus.Running;
                    var work = state.Step.Run(state.Progress, ct);
                    var minimum = Task.Delay(MinStepDuration, ct);

                    // Solange gearbeitet wird: Spinner drehen, Fortschritt neu zeichnen.
                    while (!work.IsCompleted || !minimum.IsCompleted)
                    {
                        frame++;
                        ctx.UpdateTarget(Render());
                        await Task.WhenAny(Task.WhenAll(work, minimum), Task.Delay(FrameInterval)); // ohne ct: nach Strg+C sonst Dauerschleife
                    }

                    failure = await CompleteAsync(state, work);
                    ctx.UpdateTarget(Render());
                    if (failure is not null)
                        return;
                    await Task.Delay(PauseBetweenSteps, ct);
                }

                await Task.Delay(PauseAtEnd, ct);
            });
    }

    /// <summary>Wartet auf den Schritt und hält Erfolg oder Fehler fest. Liefert den Fehler, falls einer auftrat.</summary>
    private static async Task<Exception?> CompleteAsync(StepState state, Task<string> work)
    {
        try
        {
            state.Detail = await work;
            state.Status = StepStatus.Done;
            return null;
        }
        catch (Exception e) when (e is SetupException or OperationCanceledException)
        {
            state.Status = StepStatus.Failed;
            state.Error = e switch
            {
                SetupException setup => setup.Message,
                _ when state.Progress.Read().HasProgress => "Abgebrochen. Beim nächsten Start geht es hier weiter.",
                _ => "Abgebrochen.",
            };
            return e;
        }
    }

    private static IRenderable BuildView(string title, string? subtitle, List<StepState> states, string spinnerFrame)
    {
        var rows = new List<IRenderable>
        {
            new Markup($"[bold {Theme.Tag(Theme.Accent)}]{Theme.Symbol}[/] [bold {Theme.Tag(Theme.Text)}]{Markup.Escape(title)}[/]"),
            new Markup(subtitle is null ? "" : $"[{Theme.Tag(Theme.Muted)}]{Markup.Escape(subtitle)}[/]"),
            Text.Empty,
        };

        foreach (var state in states.Where(s => s.Status != StepStatus.Pending))
        {
            rows.Add(BuildStepLine(state, spinnerFrame));

            var progress = state.Progress.Read();
            if (state.Status == StepStatus.Running && progress.HasProgress)
                rows.Add(BuildProgressLine(progress.Done, progress.Total, progress.Speed));

            if (state.Error is not null)
            {
                rows.Add(Text.Empty);
                rows.Add(new Markup($"   [{Theme.Tag(Theme.Text)}]{Markup.Escape(state.Error)}[/]"));
            }
        }

        return new Padder(new Rows(rows), new Padding(SidePadding, 1, SidePadding, 0));
    }

    private static Grid BuildStepLine(StepState state, string spinnerFrame)
    {
        var running = state.Status == StepStatus.Running;

        var icon = state.Status switch
        {
            StepStatus.Running => $"[{Theme.Tag(Theme.Accent)}]{Markup.Escape(spinnerFrame)}[/]",
            StepStatus.Failed => $"[bold {Theme.Tag(Theme.Accent)}]✗[/]",
            _ => $"[{Theme.Tag(Theme.Success)}]✓[/]",
        };
        var label = running
            ? $"[{Theme.Tag(Theme.Text)}]{Markup.Escape(state.Step.Label)} …[/]"
            : $"[{Theme.Tag(Theme.Dim)}]{Markup.Escape(state.Step.Label)}[/]";
        var detail = running ? "" : $"[{Theme.Tag(Theme.Muted)}]{Markup.Escape(state.Detail)}[/]";

        var grid = new Grid()
            .AddColumn(new GridColumn().Width(2).NoWrap().PadRight(1))
            .AddColumn(new GridColumn().Width(LabelWidth()).NoWrap())
            .AddColumn(new GridColumn().Width(DetailWidth).NoWrap().RightAligned());
        grid.AddRow(new Markup(icon), new Markup(label), new Markup(detail));
        return grid;
    }

    /// <summary>Breite der Beschriftung – passt sich schmalen Fenstern an, damit nichts überläuft.</summary>
    private static int LabelWidth()
    {
        // Rand links/rechts, Symbol-Spalte (2 + 1 Abstand), die Ergebnis-Spalte und die
        // Standard-Abstände des Grids zwischen den Spalten (je 2) abziehen.
        var available = AnsiConsole.Profile.Width - 2 * SidePadding - 3 - DetailWidth - 4;
        return Math.Clamp(available, 16, MaxLabelWidth);
    }

    private static Markup BuildProgressLine(long done, long total, double bytesPerSecond)
    {
        var fraction = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0;
        var filled = (int)(fraction * BarWidth);
        var eta = bytesPerSecond > 0 ? TimeSpan.FromSeconds((total - done) / bytesPerSecond) : TimeSpan.Zero;

        var bar = $"[{Theme.Tag(Theme.Accent)}]{new string('━', filled)}[/][{Theme.Tag(Theme.Track)}]{new string('━', BarWidth - filled)}[/]";
        var muted = Theme.Tag(Theme.Muted);

        return new Markup(
            $"   {bar}  [{Theme.Tag(Theme.Text)}]{(int)(fraction * 100),3} %[/]  " +
            $"[{muted}]{Format.Gigabytes(done)} / {Format.Gigabytes(total)} GB[/]  " +
            $"[{muted}]{Format.Speed(bytesPerSecond)}[/]  " +
            $"[{muted}]{Format.Duration(eta)}[/]");
    }
}
