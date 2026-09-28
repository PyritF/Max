using Max.Setup;

namespace Max.Memory;

/// <summary>
/// Das Gedächtnis zur Laufzeit: der aktuelle Stand, gespeichert bei jeder Änderung.
/// <paramref name="changed"/> gibt ihn weiter (neuer System-Prompt), damit Vergessenes sofort vergessen ist.
/// </summary>
/// <param name="paths">Null = nichts speichern (Demo).</param>
internal sealed class MemoryBook(MaxPaths? paths, Action<MemoryData>? changed = null)
{
    public MemoryData Current { get; private set; } = paths is null ? MemoryData.Empty : MemoryStore.Load(paths);

    public void Set(MemoryData memory)
    {
        Current = memory;
        if (paths is not null)
            MemoryStore.Save(paths, memory);
        changed?.Invoke(memory);
    }

    /// <summary>
    /// Die Begrüßung für jetzt – nur einmal: Danach ist sie verbraucht, auch wenn diesmal kein Gespräch folgt.
    /// </summary>
    public string? TakeGreeting(DateTime now)
    {
        if (Current.NextGreeting?.For(now) is not { } greeting)
            return null;
        Current = Current with { NextGreeting = null };
        if (paths is not null)
            MemoryStore.Save(paths, Current);
        return greeting;
    }
}
