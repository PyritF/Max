using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Max.Setup;

namespace Max.Memory;

/// <summary>Etwas, das Max über den Nutzer weiß – von ihm selbst am Ende eines Gesprächs notiert.</summary>
internal sealed record MemoryFact(string Text, DateOnly Added);

/// <summary>Worum es beim letzten Mal ging – für "Zuletzt" auf dem Startbildschirm.</summary>
internal sealed record LastSession(DateTime Ended, string Summary);

/// <summary>
/// Begrüßungen für den nächsten Start, schon am Ende der letzten Sitzung erzeugt – so sind sie sofort da.
/// Welche Tageszeit der nächste Start hat, weiß man vorher nicht, also eine je Tageszeit.
/// </summary>
internal sealed record Greetings(string? Morning, string? Day, string? Evening, string? Night, DateTime Created)
{
    /// <summary>Älter als das wirkt eine Begrüßung, die ans letzte Gespräch anknüpft, seltsam.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    /// <summary>Die Begrüßung zur Tageszeit – Grenzen wie bei <see cref="Ui.HomeScreen.GetGreeting"/>.</summary>
    public string? For(DateTime now)
    {
        if (now - Created > MaxAge)
            return null;
        var text = now.Hour switch
        {
            >= 5 and < 12 => Morning,
            >= 12 and < 18 => Day,
            >= 18 and < 23 => Evening,
            _ => Night,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}

/// <summary>Max' Gedächtnis – steht in <c>memory.json</c> im Datenordner und verlässt den Rechner nie.</summary>
internal sealed record MemoryData(IReadOnlyList<MemoryFact> Facts, LastSession? LastSession = null, Greetings? NextGreeting = null)
{
    /// <summary>Mehr Fakten bleiben nicht – die ältesten fallen zuerst weg.</summary>
    internal const int MaxFacts = 50;

    /// <summary>Längere "Fakten" sind keine – eher nacherzählte Gespräche.</summary>
    internal const int MaxFactLength = 160;

    public static MemoryData Empty { get; } = new([]);

    /// <summary>Hängt neue Fakten an – ohne Doppelte, ohne Leeres, höchstens <see cref="MaxFacts"/>.</summary>
    public MemoryData WithFacts(IEnumerable<string> facts, DateOnly today)
    {
        var list = Facts.ToList();
        var known = list.Select(f => Normalize(f.Text)).ToHashSet();
        foreach (var fact in facts)
        {
            var text = fact.Trim().TrimStart('-', '•', '*', ' ').Trim();
            if (text.Length is < 3 or > MaxFactLength || !known.Add(Normalize(text)))
                continue;
            list.Add(new MemoryFact(text, today));
        }
        if (list.Count > MaxFacts)
            list.RemoveRange(0, list.Count - MaxFacts);
        return this with { Facts = list };
    }

    /// <summary>Löscht den Fakt mit der Nummer (ab 1, wie bei /gedächtnis). Null, wenn es die Nummer nicht gibt.</summary>
    public MemoryData? Without(int number)
    {
        if (number < 1 || number > Facts.Count)
            return null;
        var list = Facts.ToList();
        list.RemoveAt(number - 1);
        return this with { Facts = list };
    }

    /// <summary>Zum Vergleichen: klein, nur Buchstaben und Ziffern ("Programmiert in C#." = "programmiert in c#").</summary>
    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '#' || c == '+')
                builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != ' ')
                builder.Append(' ');
        }
        return builder.ToString().Trim();
    }
}

/// <summary>Liest und schreibt <c>memory.json</c>.</summary>
internal static class MemoryStore
{
    /// <summary>Fehlt die Datei oder ist sie kaputt, fängt Max eben ohne Erinnerungen an.</summary>
    public static MemoryData Load(MaxPaths paths)
    {
        try
        {
            if (File.Exists(paths.Memory))
                return JsonSerializer.Deserialize(File.ReadAllText(paths.Memory), MemoryJson.Default.MemoryData) ?? MemoryData.Empty;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
        }
        return MemoryData.Empty;
    }

    public static void Save(MaxPaths paths, MemoryData memory)
    {
        paths.EnsureExists();
        var temp = paths.Memory + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(memory, MemoryJson.Default.MemoryData));
        File.Move(temp, paths.Memory, overwrite: true);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(MemoryData))]
internal sealed partial class MemoryJson : JsonSerializerContext;
