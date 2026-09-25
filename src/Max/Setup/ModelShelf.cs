namespace Max.Setup;

/// <summary>
/// Zum Ausprobieren anderer Stufen (<c>MAX_TIER</c> bzw. <c>--stufe</c>): Das bisherige Modell wird nicht gelöscht,
/// sondern nach <c>models/&lt;Stufe&gt;.bin</c> beiseitegelegt. Liegt die gewünschte Stufe dort schon, kommt sie
/// zurück an ihren Platz – sonst lädt die Einrichtung sie herunter. Verschieben geht sofort, auch bei vielen GB.
/// </summary>
internal static class ModelShelf
{
    public static string Directory(MaxPaths paths) => Path.Combine(paths.Root, "models");

    public static string ModelFile(MaxPaths paths, string tier) => Path.Combine(Directory(paths), tier + ".bin");

    public static string StateFile(MaxPaths paths, string tier) => Path.Combine(Directory(paths), tier + ".json");

    /// <summary>Stufen, die beiseitegelegt sind.</summary>
    public static IEnumerable<string> Shelved(MaxPaths paths) =>
        System.IO.Directory.Exists(Directory(paths))
            ? System.IO.Directory.EnumerateFiles(Directory(paths), "*.bin").Select(f => Path.GetFileNameWithoutExtension(f)!).Order()
            : [];

    /// <summary>Sorgt dafür, dass am Modell-Platz die gewünschte Stufe liegt – oder nichts (dann folgt die Einrichtung).</summary>
    /// <returns>true, wenn etwas verschoben wurde.</returns>
    public static bool Prepare(MaxPaths paths, Tier desired)
    {
        var wanted = desired.ToString();
        var current = InstallState.Load(paths);
        if (current is not null && current.Tier.Equals(wanted, StringComparison.OrdinalIgnoreCase) && File.Exists(paths.Model))
            return false;

        System.IO.Directory.CreateDirectory(Directory(paths));

        // Bisheriges Modell beiseitelegen.
        if (current is not null && File.Exists(paths.Model))
        {
            File.Move(paths.Model, ModelFile(paths, current.Tier), overwrite: true);
            File.Move(paths.State, StateFile(paths, current.Tier), overwrite: true);
        }
        else if (File.Exists(paths.State))
        {
            File.Delete(paths.State);
        }

        // Ein angefangener Download gehört womöglich zu einer anderen Stufe.
        if (File.Exists(paths.ModelPart))
            File.Delete(paths.ModelPart);

        // Gewünschte Stufe zurückholen, falls vorhanden.
        if (File.Exists(ModelFile(paths, wanted)) && File.Exists(StateFile(paths, wanted)))
        {
            File.Move(ModelFile(paths, wanted), paths.Model, overwrite: true);
            File.Move(StateFile(paths, wanted), paths.State, overwrite: true);
        }
        return true;
    }
}
