using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Max.Chat;

/// <summary>Ein gespeichertes Gespräch in der Liste von /verlauf.</summary>
internal sealed record SavedConversation(string Id, DateTime Started, DateTime Updated, string Title, int Count);

/// <summary>
/// Die Gespräche, als JSON im Datenordner (<c>gespraeche/</c>) – damit Max mit /weiter dort anknüpfen kann, wo ihr
/// aufgehört habt. Gespeichert wird nach jeder Antwort; die neuesten <see cref="Keep"/> bleiben, ältere fallen weg.
/// Alles bleibt auf dem Rechner, /verlauf löschen räumt auf.
/// </summary>
internal sealed class ConversationArchive(string folder)
{
    internal const int Keep = 50;

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Speichert das Gespräch (bekommt dabei einen Namen) – nur, wenn der Nutzer etwas gesagt hat.</summary>
    public void Save(Conversation conversation, string? title = null)
    {
        var messages = conversation.Messages;
        if (!messages.Any(m => m.Role == ChatRole.User))
            return;
        Directory.CreateDirectory(folder);
        conversation.Id ??= NewId(messages[0].Timestamp);
        var path = PathOf(conversation.Id);
        title ??= Read(path)?.Title;
        var file = new ArchiveFile(1, messages[0].Timestamp, DateTime.Now, title, conversation.Summary, conversation.SummarizedCount,
            messages.Select(m => new ArchiveMessage(m.Role.ToString().ToLowerInvariant(), m.Content, m.Timestamp)).ToList());
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, ArchiveJson.Default.ArchiveFile));
        File.Move(temp, path, overwrite: true);
        Prune();
    }

    /// <summary>Die gespeicherten Gespräche, das zuletzt geführte zuerst.</summary>
    public IReadOnlyList<SavedConversation> List()
    {
        if (!Directory.Exists(folder))
            return [];
        return Directory.EnumerateFiles(folder, "*.json")
            .Select(path => (Id: Path.GetFileNameWithoutExtension(path), File: Read(path)))
            .Where(x => x.File is { Messages.Count: > 0 })
            .Select(x => new SavedConversation(x.Id, x.File!.Started, x.File.Updated, TitleOf(x.File), x.File.Messages.Count))
            .OrderByDescending(c => c.Updated)
            .ToList();
    }

    /// <summary>Lädt ein Gespräch in <paramref name="into"/> – false, wenn es das nicht (mehr) gibt.</summary>
    public bool Load(string id, Conversation into)
    {
        if (Read(PathOf(id)) is not { } file)
            return false;
        var messages = file.Messages.Select(m => new ChatMessage(
            Enum.TryParse<ChatRole>(m.Role, ignoreCase: true, out var role) ? role : ChatRole.User, m.Content, m.Time));
        into.Load(id, messages, file.Summary, file.SummarizedCount);
        return true;
    }

    /// <summary>Löscht alle gespeicherten Gespräche; gibt zurück, wie viele es waren.</summary>
    public int DeleteAll()
    {
        if (!Directory.Exists(folder))
            return 0;
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            File.Delete(path);
            count++;
        }
        return count;
    }

    /// <summary>"heute, 09:12", "gestern, 23:41", "Mo, 28.9., 14:05".</summary>
    public static string When(DateTime time, DateTime now) =>
        time.Date == now.Date ? $"heute, {time:HH:mm}"
        : time.Date == now.Date.AddDays(-1) ? $"gestern, {time:HH:mm}"
        : time.ToString("ddd, d.M., HH:mm", German);

    private string PathOf(string id) => Path.Combine(folder, id + ".json");

    private string NewId(DateTime started)
    {
        var id = started.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        for (var n = 2; File.Exists(PathOf(id)); n++)
            id = started.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + "_" + n;
        return id;
    }

    private void Prune()
    {
        foreach (var old in List().Skip(Keep))
        {
            try { File.Delete(PathOf(old.Id)); } catch (IOException) { }
        }
    }

    private static ArchiveFile? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), ArchiveJson.Default.ArchiveFile) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Der Titel – sonst der Anfang der ersten Frage.</summary>
    private static string TitleOf(ArchiveFile file)
    {
        if (file.Title is { Length: > 0 } title)
            return title;
        var first = file.Messages.FirstOrDefault(m => m.Role == "user")?.Content.ReplaceLineEndings(" ").Trim() ?? "";
        return first.Length <= 60 ? first : first[..57].TrimEnd() + "…";
    }
}

internal sealed record ArchiveFile(int Version, DateTime Started, DateTime Updated, string? Title, string? Summary, int SummarizedCount,
    List<ArchiveMessage> Messages);

internal sealed record ArchiveMessage(string Role, string Content, DateTime Time);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ArchiveFile))]
internal sealed partial class ArchiveJson : JsonSerializerContext;
