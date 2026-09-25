using System.Globalization;
using Max.Setup;
using Max.Ui;

namespace Max.Llm;

/// <summary>Die Zeilen für /debug.</summary>
internal static class DebugReport
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static IReadOnlyList<(string Label, string Value)> Build(LlmEngine? engine, LlmBackend? backend, MaxPaths paths, SystemSnapshot system)
    {
        var state = InstallState.Load(paths);
        var rows = new List<(string, string)>
        {
            ("Version", typeof(DebugReport).Assembly.GetName().Version?.ToString(3) ?? "?"),
            ("Stufe", state?.Tier ?? "–"),
            ("Modelldatei", ModelFileName(state) ?? "–"),
        };

        if (engine is not null)
        {
            var info = engine.Info;
            rows.Add(("Modell", $"{info.Description} ({info.Architecture})"));
            rows.Add(("Backend", info.GpuLayers > 0 ? $"{info.Backend} · {Math.Min(info.GpuLayers, info.LayerCount)}/{info.LayerCount} Schichten auf der GPU" : "CPU"));
            rows.Add(("Kontext", $"{engine.CachedTokens:N0} / {info.ContextSize:N0} Tokens".Replace(',', '.')));
            rows.Add(("Ladezeit", Seconds(info.LoadTime)));
            if (backend?.WarmUpTime is { } warmUp)
                rows.Add(("Aufwärmen", Seconds(warmUp)));

            if (engine.LastRun is { } run)
            {
                rows.Add(("Letzte Antwort", $"{run.TokensPerSecond.ToString("0.0", German)} Tokens/s · {run.GeneratedTokens} Tokens"));
                rows.Add(("Erstes Token nach", $"{Seconds(run.TimeToFirstToken)} · Prompt {run.PromptTokens} Tokens, davon {run.ReusedTokens} aus dem Cache"));
            }
        }
        else
        {
            rows.Add(("Modell", "nicht geladen (Platzhalter-Antworten)"));
        }

        var gpu = system.Hardware.Gpu;
        rows.Add(("GPU", gpu is null ? "keine erkannt" : $"{gpu.Name} · {Format.Memory(gpu.VramBytes)}"));
        rows.Add(("RAM", Format.Memory(system.Hardware.RamBytes)));
        rows.Add(("Datenordner", paths.Root));
        if (ModelShelf.Shelved(paths).ToList() is { Count: > 0 } shelved)
            rows.Add(("Beiseitegelegt", string.Join(", ", shelved) + " (mit --stufe wechseln)"));
        return rows;
    }

    /// <summary>Dateiname aus dem Manifest, z. B. "Qwen3.5-4B-Q4_K_M.gguf" – nur hier sichtbar, nie im normalen Betrieb.</summary>
    private static string? ModelFileName(InstallState? state)
    {
        if (state is null || !TierSelector.TryParse(state.Tier, out var tier))
            return null;
        var url = ManifestSource.Embedded().For(tier)?.Url;
        return url is null ? null : Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]);
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.0", German) + " s";
}
