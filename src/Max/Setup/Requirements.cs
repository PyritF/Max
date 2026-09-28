namespace Max.Setup;

/// <summary>
/// Was ein Rechner für Max mitbringen muss. Es gibt ein einziges Modell (9B); ohne Grafikkarte wären die
/// Antworten zu langsam (eine halbe bis ganze Minute), mit weniger Arbeitsspeicher passt es nicht.
/// Passt das Modell nicht ganz auf die Grafikkarte (unter 6–8 GB), rechnet der Prozessor den Rest –
/// langsamer, aber brauchbar; dafür muss der Rest in den Arbeitsspeicher passen.
/// Grenzwerte in GB wie auf dem Karton – Rechner melden meist etwas weniger (15,8 statt 16 GB).
/// </summary>
internal static class Requirements
{
    public const double MinVramGb = 4;
    public const double MinRamGb = 8;

    /// <summary>Unter dieser Größe liegt ein guter Teil des Modells im Arbeitsspeicher.</summary>
    public const double SmallVramGb = 6;
    public const double MinRamWithSmallVramGb = 16;
    private const double Tolerance = 0.9;

    /// <summary><c>MAX_CPU=1</c> erlaubt den Start ohne Grafikkarte – für die Tests auf GitHub und falls eine Karte nicht erkannt wird.</summary>
    public static bool CpuAllowed => Environment.GetEnvironmentVariable("MAX_CPU") == "1";

    /// <summary>Was fehlt, als Satz für den Nutzer – oder null, wenn alles passt.</summary>
    public static string? Problem(HardwareInfo hw, bool cpuAllowed)
    {
        if (hw.RamBytes < Bytes(MinRamGb))
            return $"Ich brauche mindestens {MinRamGb:0} GB Arbeitsspeicher – dieser Rechner hat {Gb(hw.RamBytes)} GB.";
        if (cpuAllowed)
            return null;
        if (hw.Gpu is null)
            return $"Ich brauche eine Grafikkarte mit mindestens {MinVramGb:0} GB Speicher – auf diesem Rechner habe ich keine gefunden.";
        if (hw.VramBytes < Bytes(MinVramGb))
            return $"Ich brauche eine Grafikkarte mit mindestens {MinVramGb:0} GB Speicher – die {hw.Gpu.Name} hat {Gb(hw.VramBytes)} GB.";
        if (hw.VramBytes < Bytes(SmallVramGb) && hw.RamBytes < Bytes(MinRamWithSmallVramGb))
            return $"Mit einer Grafikkarte unter {SmallVramGb:0} GB brauche ich mindestens {MinRamWithSmallVramGb:0} GB Arbeitsspeicher – dieser Rechner hat {Gb(hw.RamBytes)} GB.";
        return null;
    }

    private static long Bytes(double gb) => (long)(gb * Tolerance * 1024 * 1024 * 1024);

    private static string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
}
