namespace Max.Ui;

/// <summary>
/// Ein Schritt der Startsequenz, z. B. "Analysiere Hardware".
/// </summary>
/// <param name="Label">Was gerade passiert.</param>
/// <param name="Run">
/// Die eigentliche Arbeit. Liefert den kurzen Ergebnistext, der rechts neben dem Häkchen steht
/// (z. B. "aktuell"). Über <see cref="StepProgress"/> kann ein Download seinen Fortschritt melden.
/// </param>
internal sealed record StartupStep(string Label, Func<StepProgress, CancellationToken, Task<string>> Run);

/// <summary>
/// Fortschritt eines laufenden Schritts. Sobald <see cref="Report"/> einmal aufgerufen wurde,
/// zeigt die Startsequenz unter dem Schritt einen Fortschrittsbalken.
/// </summary>
internal sealed class StepProgress
{
    private readonly object _lock = new();

    public bool HasProgress { get; private set; }
    public long BytesDone { get; private set; }
    public long BytesTotal { get; private set; }
    public double BytesPerSecond { get; private set; }

    public void Report(long bytesDone, long bytesTotal, double bytesPerSecond)
    {
        lock (_lock)
        {
            HasProgress = true;
            BytesDone = bytesDone;
            BytesTotal = bytesTotal;
            BytesPerSecond = bytesPerSecond;
        }
    }

    /// <summary>Liest alle Werte auf einmal, damit sie zueinander passen (Report läuft evtl. in einem anderen Thread).</summary>
    public (bool HasProgress, long Done, long Total, double Speed) Read()
    {
        lock (_lock)
            return (HasProgress, BytesDone, BytesTotal, BytesPerSecond);
    }
}
