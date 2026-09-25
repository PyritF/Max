namespace Max.Setup;

/// <summary>Leistungsstufe – rein intern, der Nutzer sieht sie nie (höchstens unter /debug).</summary>
internal enum Tier { S, M, L, XL }

/// <summary>
/// Grenzwerte für die Stufen, in GB wie auf dem Karton.
/// Rechner melden meist etwas weniger (z. B. 15,8 statt 16 GB) – das fängt <see cref="Tolerance"/> ab.
/// </summary>
internal sealed record TierRules(
    double XlVramGb = 16,
    double XlRamGb = 48,
    double LVramGb = 8,
    double MVramGb = 6,
    double MRamGb = 8,
    double Tolerance = 0.9)
{
    public static TierRules Default { get; } = new();

    internal long Bytes(double gb) => (long)(gb * Tolerance * 1024 * 1024 * 1024);
}

internal static class TierSelector
{
    /// <summary>
    /// Wählt die Stufe:
    /// XL ab 16 GB Grafikspeicher oder 48 GB RAM, L ab 8 GB Grafikspeicher,
    /// M ab 6 GB Grafikspeicher oder 8 GB RAM, sonst S.
    /// </summary>
    public static Tier Select(HardwareInfo hw, TierRules? rules = null)
    {
        rules ??= TierRules.Default;
        var vram = hw.VramBytes;
        var ram = hw.RamBytes;

        if (vram >= rules.Bytes(rules.XlVramGb) || ram >= rules.Bytes(rules.XlRamGb))
            return Tier.XL;
        if (vram >= rules.Bytes(rules.LVramGb))
            return Tier.L;
        if (vram >= rules.Bytes(rules.MVramGb) || ram >= rules.Bytes(rules.MRamGb))
            return Tier.M;
        return Tier.S;
    }

    /// <summary>Wie <see cref="Select"/>, aber <c>MAX_TIER=S|M|L|XL</c> hat Vorrang (Tests, Fehlersuche).</summary>
    public static Tier Resolve(HardwareInfo hw, string? overrideValue = null)
    {
        overrideValue ??= Environment.GetEnvironmentVariable("MAX_TIER");
        return TryParse(overrideValue, out var forced) ? forced : Select(hw);
    }

    public static bool TryParse(string? value, out Tier tier) =>
        Enum.TryParse(value?.Trim(), ignoreCase: true, out tier) && Enum.IsDefined(tier);
}
