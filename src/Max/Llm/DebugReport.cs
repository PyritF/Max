using System.Globalization;
using Max.Setup;
using Max.Ui;

namespace Max.Llm;

/// <summary>Die Zeilen für /debug.</summary>
internal static class DebugReport
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static IReadOnlyList<(string Label, string Value)> Build(LlmEngine? engine, LlmBackend? backend, MaxPaths paths, SystemSnapshot system, Update.Updater? updater = null)
    {
        var state = InstallState.Load(paths);
        var rows = new List<(string, string)>
        {
            ("Version", typeof(DebugReport).Assembly.GetName().Version?.ToString(3) ?? "?"),
            ("Modelldatei", state?.Model ?? "–"),
        };

        if (engine is not null)
        {
            var info = engine.Info;
            rows.Add(("Modell", $"{info.Description} ({info.Architecture})"));
            rows.Add(("Backend", info.GpuLayers > 0 ? $"{info.Backend} · {Math.Min(info.GpuLayers, info.LayerCount)}/{info.LayerCount} Schichten auf der GPU"
                : system.Hardware.Gpu is not null ? "CPU (Grafikkarte fehlgeschlagen)" : "CPU"));
            rows.Add(("Kontext", $"{engine.CachedCount:N0} / {info.ContextSize:N0} Tokens".Replace(',', '.')));
            rows.Add(("Ladezeit", Seconds(info.LoadTime)));
            rows.Add(("Adapter", info.Adapter ?? "keiner"));
            if (backend?.WarmUpTime is { } warmUp)
                rows.Add(("Aufwärmen", Seconds(warmUp) + (backend.WarmUpFromCache ? " · aus dem Zwischenspeicher" : "")));

            if (backend is not null)
                rows.Add(("Denken", backend.ThinkingEnabled ? "an" : "aus"));
            if (backend?.LastRun is { } run)
            {
                rows.Add(("Letzte Antwort", $"{run.TokensPerSecond.ToString("0.0", German)} Tokens/s · {run.GeneratedTokens} Tokens"));
                if (run.ThinkingTokens > 0)
                    rows.Add(("Nachgedacht", $"{Seconds(run.ThinkingTime)} · {run.ThinkingTokens} Tokens"));
                rows.Add(("Erstes Token nach", $"{Seconds(run.TimeToFirstToken)} · Prompt {run.PromptTokens} Tokens, davon {run.ReusedTokens} aus dem Cache"));
                if (run.Repairs > 0)
                    rows.Add(("Reparaturen", run.Repairs.ToString(German)));
            }
        }
        else
        {
            rows.Add(("Modell", "nicht geladen (Platzhalter-Antworten)"));
        }

        var gpu = system.Hardware.Gpu;
        rows.Add(("GPU", gpu is null ? "keine erkannt" : $"{gpu.Name} · {Format.Memory(gpu.VramBytes)}"));
        rows.Add(("RAM", Format.Memory(system.Hardware.RamBytes)));
        rows.Add(("Bilder", Update.AddOnFile.Vision.Installed(paths) is not null ? "Bild-Zusatz bereit" : "Bild-Zusatz fehlt noch (kommt im Hintergrund)"));
        rows.Add(("Sprache", Update.AddOnFile.Audio.Installed(paths) is not null ? "Spracherkennung bereit" : "Spracherkennung fehlt noch (kommt im Hintergrund)"));
        if (updater is not null)
            rows.Add(("Update", UpdateText(updater)));
        rows.Add(("Datenordner", paths.Root));
        return rows;
    }

    private static string UpdateText(Update.Updater updater)
    {
        var (hasProgress, done, total, _) = updater.Progress.Read();
        return hasProgress && total > 0 && done < total
            ? $"{updater.Status} ({done * 100 / total} %)"
            : updater.Status;
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.0", German) + " s";
}
