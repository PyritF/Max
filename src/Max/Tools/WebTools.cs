using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Max.Tools;

/// <summary>
/// Sucht im Web über DuckDuckGo (ohne Schlüssel oder Konto) und liefert die ersten Treffer mit Titel, Adresse und Auszug.
/// Die Suchanfrage verlässt dabei den Rechner – das sieht der Nutzer in der Anzeige ("Suche im Web: …").
/// </summary>
internal sealed partial class WebSearchTool(HttpClient http) : ITool
{
    internal const int MaxResults = 6;

    public string Name => "websuche";
    public string? Argument => "Suchbegriffe";
    public string Description => "Sucht im Internet – für Aktuelles, Zahlen, Fakten, die du nicht sicher weißt. Liefert Treffer mit Adresse und Auszug.";
    public string Describe(string argument) => $"Suche im Web: {argument}";

    public async Task<string> RunAsync(string argument, CancellationToken ct)
    {
        var url = "https://html.duckduckgo.com/html/?kl=de-de&q=" + Uri.EscapeDataString(argument);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", WebPage.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", "de-DE,de;q=0.9,en;q=0.5");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return $"Die Websuche antwortet gerade nicht ({(int)response.StatusCode}).";
        var html = await response.Content.ReadAsStringAsync(ct);
        var results = Parse(html);
        if (results.Count == 0)
            return html.Contains("anomaly", StringComparison.OrdinalIgnoreCase) || html.Contains("captcha", StringComparison.OrdinalIgnoreCase)
                ? "Die Websuche lässt mich gerade nicht suchen (Schutz gegen Automaten). Später noch einmal versuchen."
                : $"Keine Treffer für \"{argument}\".";

        var text = new StringBuilder($"Treffer für \"{argument}\":\n");
        for (var i = 0; i < results.Count; i++)
        {
            var (title, link, snippet) = results[i];
            text.Append(i + 1).Append(". ").Append(title).Append('\n').Append("   ").Append(link).Append('\n');
            if (snippet.Length > 0)
                text.Append("   ").Append(snippet).Append('\n');
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>Die Treffer aus der HTML-Seite von DuckDuckGo.</summary>
    internal static List<(string Title, string Url, string Snippet)> Parse(string html)
    {
        var results = new List<(string, string, string)>();
        var snippets = SnippetRegex().Matches(html).Select(m => WebPage.Clean(m.Groups[1].Value)).ToList();
        var links = LinkRegex().Matches(html);
        for (var i = 0; i < links.Count && results.Count < MaxResults; i++)
        {
            var link = RealUrl(WebUtility.HtmlDecode(links[i].Groups[1].Value));
            if (link is null || link.Contains("duckduckgo.com/y.js", StringComparison.Ordinal))      // Werbung
                continue;
            results.Add((WebPage.Clean(links[i].Groups[2].Value), link, i < snippets.Count ? snippets[i] : ""));
        }
        return results;
    }

    /// <summary>DuckDuckGo leitet über "//duckduckgo.com/l/?uddg=&lt;Adresse&gt;" – die eigentliche Adresse steht in uddg.</summary>
    internal static string? RealUrl(string href)
    {
        if (href.StartsWith("//", StringComparison.Ordinal))
            href = "https:" + href;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
            return null;
        if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/l/")
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&'))
                if (part.StartsWith("uddg=", StringComparison.Ordinal))
                    return Uri.UnescapeDataString(part[5..]);
            return null;
        }
        return uri.Scheme is "http" or "https" ? uri.ToString() : null;
    }

    [GeneratedRegex("""<a[^>]*class="result__a"[^>]*href="([^"]+)"[^>]*>(.*?)</a>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex("""class="result__snippet"[^>]*>(.*?)</(?:a|td|div)>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SnippetRegex();
}

/// <summary>
/// Liest eine Webseite und liefert ihren Text – ohne Menüs, Skripte und Formatierung. Lange Seiten wie Dokumente:
/// der Anfang, mit " | Suchbegriff" die passenden Stellen, mit " | Teil 3" weiter hinten (siehe <see cref="Documents.DocumentView"/>).
/// </summary>
internal sealed class ReadWebPageTool(HttpClient http) : ITool
{
    /// <summary>Mehr wird nicht geladen.</summary>
    internal const int MaxBytes = 2 * 1024 * 1024;

    // Eine Seite, in der Max gerade blättert, nicht jedes Mal neu laden – aber nach ein paar Minuten schon.
    private readonly RecentCache<WebDocument> _recent = new(4, TimeSpan.FromMinutes(10));

    private sealed record WebDocument(string Title, Documents.Document Document);

    public string Name => "webseite";
    public string? Argument => "Adresse, optional mit | Suchbegriff oder | Teil 2";
    public string Description => "Liest den Text einer Webseite – z. B. einen Treffer aus der Websuche, um Genaueres zu erfahren. Lange Seiten: „| Suchbegriff“ zeigt die passenden Stellen.";
    public string Describe(string argument) => ReadFileTool.Describe("Lese", argument);

    public async Task<string> RunAsync(string argument, CancellationToken ct)
    {
        var (target, selector) = ReadFileTool.Split(argument);
        var address = target.Trim().Trim('<', '>', '"');
        if (!address.Contains("://", StringComparison.Ordinal))
            address = "https://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return $"\"{target}\" ist keine Webadresse.";

        WebDocument page;
        try
        {
            page = await _recent.GetAsync(uri.ToString(), () => LoadAsync(uri, ct));
        }
        catch (Documents.DocumentException e)
        {
            return e.Message;
        }
        return await Documents.DocumentView.RenderAsync(page.Document, page.Title, $"{Name}: {target}", selector, ct);
    }

    private async Task<WebDocument> LoadAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", WebPage.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", "de-DE,de;q=0.9,en;q=0.5");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new Documents.DocumentException($"Die Seite antwortet mit Fehler {(int)response.StatusCode}.");
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Length > 0 && !type.StartsWith("text/", StringComparison.Ordinal) && !type.Contains("html") && !type.Contains("json") && !type.Contains("xml"))
            throw new Documents.DocumentException($"Die Adresse liefert keinen Text, sondern {type}.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBytes];
        var read = 0;
        while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), ct) is var n and > 0)
            read += n;
        var encoding = response.Content.Headers.ContentType?.CharSet is { Length: > 0 } charset ? TryEncoding(charset) : Encoding.UTF8;
        var content = encoding.GetString(buffer, 0, read);
        var address = response.RequestMessage?.RequestUri ?? uri;
        if (!type.Contains("html") && !content.TrimStart().StartsWith('<'))
            return new WebDocument($"Webseite {address}", Documents.DocumentReader.Chunked("Webseite", Documents.DocumentReader.SplitLines(content)));
        var (title, text) = WebPage.ToText(content);
        if (text.Trim().Length == 0)
            throw new Documents.DocumentException($"Auf {address} steht kein lesbarer Text (vermutlich baut erst ein Skript die Seite auf).");
        return new WebDocument($"Webseite {address}{(title.Length > 0 ? $" („{title}“)" : "")}",
            Documents.DocumentReader.Chunked("Webseite", Documents.DocumentReader.SplitLines(text)));
    }

    private static Encoding TryEncoding(string name)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(name.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}

/// <summary>HTML → lesbarer Text.</summary>
internal static partial class WebPage
{
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36";

    public static (string Title, string Text) ToText(string html)
    {
        var title = TitleRegex().Match(html) is { Success: true } m ? Clean(m.Groups[1].Value) : "";
        var body = BodyRegex().Match(html) is { Success: true } b ? b.Groups[1].Value : html;
        body = HiddenRegex().Replace(body, " ");
        body = CommentRegex().Replace(body, " ");
        body = BreakRegex().Replace(body, "\n");
        body = TagRegex().Replace(body, " ");
        body = WebUtility.HtmlDecode(body);

        var lines = new List<string>();
        foreach (var raw in body.Split('\n'))
        {
            var line = SpaceRegex().Replace(raw, " ").Trim();
            if (line.Length > 0 && (lines.Count == 0 || lines[^1] != line))
                lines.Add(line);
        }
        return (title, string.Join('\n', lines));
    }

    /// <summary>Ein Stück HTML als einzeiliger Text.</summary>
    public static string Clean(string html) => SpaceRegex().Replace(WebUtility.HtmlDecode(TagRegex().Replace(html, "")), " ").Trim();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex BodyRegex();

    [GeneratedRegex(@"<(script|style|noscript|svg|nav|footer|header|form|iframe|template|aside)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HiddenRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"<(br|/p|/div|/li|/tr|/h[1-6]|/section|/article|/table|/ul|/ol|/blockquote|/pre)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t\r\f\v ]+")]
    private static partial Regex SpaceRegex();
}
