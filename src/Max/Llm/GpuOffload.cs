namespace Max.Llm;

/// <summary>Wie viele Schichten des Modells auf die Grafikkarte kommen.</summary>
internal static class GpuOffload
{
    /// <summary>Anteil des Grafikspeichers, den das Modell belegen darf – der Rest bleibt für Kontext und System.</summary>
    internal const double Budget = 0.8;

    /// <summary>"Alle Schichten" – llama.cpp begrenzt das selbst auf die tatsächliche Zahl.</summary>
    internal const int All = 999;

    public static int Layers(long modelBytes, int blockCount, long vramBytes)
    {
        if (vramBytes <= 0 || blockCount <= 0 || modelBytes <= 0)
            return 0;

        var budget = vramBytes * Budget;
        if (modelBytes <= budget)
            return All;

        // Passt nicht ganz: grob anteilig. Die Schichten sind etwa gleich groß.
        return Math.Clamp((int)(blockCount * budget / modelBytes), 0, blockCount);
    }
}
