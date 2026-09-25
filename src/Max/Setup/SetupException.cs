namespace Max.Setup;

/// <summary>
/// Ein Fehler bei der Einrichtung, dessen Text direkt für den Nutzer gedacht ist –
/// ohne Stacktrace, ohne Technik-Begriffe.
/// </summary>
internal sealed class SetupException(string message, Exception? inner = null) : Exception(message, inner);
