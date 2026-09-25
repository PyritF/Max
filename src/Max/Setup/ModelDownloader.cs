using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Max.Ui;

namespace Max.Setup;

/// <param name="Sha256">Geprüfte Prüfsumme (klein geschrieben, hex).</param>
/// <param name="SizeBytes">Tatsächliche Größe der Datei.</param>
internal sealed record DownloadResult(string Sha256, long SizeBytes);

/// <summary>
/// Lädt das Modell nach <see cref="MaxPaths.ModelPart"/> – fortsetzbar und mit SHA-256-Prüfung.
/// Umbenannt wird erst danach (<see cref="InstallState.Commit"/>).
/// </summary>
/// <param name="http">
/// Muss mit <c>AllowAutoRedirect = false</c> erstellt sein: Hugging Face nennt Prüfsumme und Größe
/// nur in der Weiterleitung (<c>X-Linked-ETag</c>, <c>X-Linked-Size</c>), deshalb folgt Max ihr selbst.
/// </param>
/// <param name="freeSpace">Freier Platz für einen Ordner in Bytes; austauschbar für Tests.</param>
internal sealed class ModelDownloader(HttpClient http, Func<string, long>? freeSpace = null)
{
    private const int BufferSize = 1 << 20;
    private const int MaxRedirects = 10;

    /// <summary>Kommt so lange nichts an, gilt die Verbindung als abgebrochen.</summary>
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    private readonly Func<string, long> _freeSpace = freeSpace ?? DefaultFreeSpace;

    public async Task<DownloadResult> DownloadAsync(TierEntry entry, MaxPaths paths, StepProgress progress, CancellationToken ct)
    {
        paths.EnsureExists();

        // Zwei Anläufe: Passt die Teil-Datei nicht zum Server (416), einmal von vorn.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await DownloadOnceAsync(entry, paths, progress, ct);
            }
            catch (RangeNotSatisfiableException) when (attempt == 0)
            {
                File.Delete(paths.ModelPart);
            }
        }
    }

    private async Task<DownloadResult> DownloadOnceAsync(TierEntry entry, MaxPaths paths, StepProgress progress, CancellationToken ct)
    {
        var existing = File.Exists(paths.ModelPart) ? new FileInfo(paths.ModelPart).Length : 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Beim Fortsetzen muss die Prüfsumme den schon vorhandenen Teil mit abdecken.
        if (existing > 0)
            await HashExistingAsync(paths.ModelPart, hash, existing, Math.Max(entry.SizeBytes, existing), progress, ct);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);

        var (response, linked) = await SendAsync(entry.Url, existing, stall.Token, ct);
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                throw new RangeNotSatisfiableException();
            if (!response.IsSuccessStatusCode)
                throw new SetupException($"Der Download ist gerade nicht verfügbar (Fehler {(int)response.StatusCode}). Versuch es später noch einmal.");

            var resume = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!resume && existing > 0)
            {
                // Der Server liefert trotz Range die ganze Datei – also von vorn.
                existing = 0;
                hash.GetHashAndReset();
            }

            var total = response.Content.Headers.ContentRange?.Length
                        ?? (response.Content.Headers.ContentLength is { } length ? existing + length : (long?)null)
                        ?? linked.Size
                        ?? entry.SizeBytes;
            var expected = NormalizeSha(entry.Sha256) ?? linked.Sha256
                ?? throw new SetupException("Der Download lässt sich nicht prüfen. Versuch es später noch einmal.");

            EnsureSpace(paths.Root, total - existing);

            var written = await CopyAsync(response, paths.ModelPart, resume, hash, existing, total, progress, stall, ct);
            if (total > 0 && written != total)
                throw Interrupted();

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(paths.ModelPart);
                throw new SetupException("Der Download ist beschädigt. Starte Max einfach neu.");
            }

            return new DownloadResult(actual, written);
        }
    }

    private static async Task HashExistingAsync(string file, IncrementalHash hash, long length, long total, StepProgress progress, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        long done = 0;
        int read;
        while (done < length && (read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - done)), ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            progress.Report(done, total, 0);
        }
    }

    /// <summary>Stellt die Anfrage und folgt Weiterleitungen selbst, um die Prüfsumme von Hugging Face mitzunehmen.</summary>
    private async Task<(HttpResponseMessage Response, LinkedInfo Linked)> SendAsync(string url, long from, CancellationToken token, CancellationToken userToken)
    {
        var current = new Uri(url);
        var linked = new LinkedInfo(null, null);

        for (var i = 0; i <= MaxRedirects; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (from > 0)
                request.Headers.Range = new RangeHeaderValue(from, null);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            }
            catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !userToken.IsCancellationRequested))
            {
                throw new SetupException("Keine Verbindung. Für die Einrichtung braucht Max einmalig Internet.", e);
            }

            linked = linked.Merge(LinkedInfo.From(response.Headers));

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                response.Dispose();
                continue;
            }

            return (response, linked);
        }

        throw new SetupException("Der Download ist gerade nicht verfügbar (zu viele Weiterleitungen).");
    }

    private async Task<long> CopyAsync(
        HttpResponseMessage response, string file, bool append, IncrementalHash hash,
        long existing, long total, StepProgress progress, CancellationTokenSource stall, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        var speed = new SpeedMeter();
        var done = existing;
        speed.Add(done);
        progress.Report(done, total, 0);

        await using var body = await response.Content.ReadAsStreamAsync(stall.Token);
        await using var output = new FileStream(file, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);

        while (true)
        {
            int read;
            try
            {
                stall.CancelAfter(StallTimeout); // Frist bei jedem Lesen neu starten
                read = await body.ReadAsync(buffer, stall.Token);
            }
            catch (Exception e) when (e is IOException or HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                await output.FlushAsync(CancellationToken.None);
                throw Interrupted(e);
            }

            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            hash.AppendData(buffer, 0, read);
            done += read;
            progress.Report(done, total, speed.Add(done));
        }

        await output.FlushAsync(ct);
        return done;
    }

    private void EnsureSpace(string folder, long needed)
    {
        // 10 % Reserve – eine randvolle Platte macht niemandem Freude.
        var required = needed + needed / 10;
        var free = _freeSpace(folder);
        if (free < required)
            throw new SetupException($"Zu wenig Speicherplatz: {Format.Gigabytes(required)} GB nötig, {Format.Gigabytes(free)} GB frei.");
    }

    private static long DefaultFreeSpace(string folder)
    {
        try { return new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace; }
        catch { return long.MaxValue; } // lässt sich nicht ermitteln → nicht blockieren
    }

    private static SetupException Interrupted(Exception? inner = null) =>
        new("Die Verbindung ist abgebrochen. Starte Max neu – der Download geht dann dort weiter.", inner);

    /// <summary>"sha256:ABC…", "\"abc…\"" → "abc…"; alles, was nicht wie SHA-256 aussieht → null.</summary>
    internal static string? NormalizeSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var v = value.Trim();
        if (v.StartsWith("W/", StringComparison.Ordinal))
            v = v[2..];
        v = v.Trim('"');
        if (v.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            v = v[7..];
        return v.Length == 64 && v.All(Uri.IsHexDigit) ? v.ToLowerInvariant() : null;
    }

    private sealed class RangeNotSatisfiableException : Exception;

    private sealed record LinkedInfo(string? Sha256, long? Size)
    {
        public static LinkedInfo From(HttpResponseHeaders headers) => new(
            headers.TryGetValues("X-Linked-ETag", out var etag) ? NormalizeSha(etag.FirstOrDefault()) : null,
            headers.TryGetValues("X-Linked-Size", out var size) && long.TryParse(size.FirstOrDefault(), out var s) ? s : null);

        public LinkedInfo Merge(LinkedInfo other) => new(other.Sha256 ?? Sha256, other.Size ?? Size);
    }
}

/// <summary>Geschwindigkeit, gemittelt über ungefähr die letzte Sekunde – sonst zappelt die Anzeige.</summary>
internal sealed class SpeedMeter(TimeSpan? window = null, Func<TimeSpan>? clock = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(1);
    private readonly Func<TimeSpan> _clock = clock ?? StopwatchClock();
    private readonly Queue<(TimeSpan Time, long Bytes)> _samples = new();

    /// <summary>Neuer Stand; liefert Bytes pro Sekunde (0, solange es zu wenig Messwerte gibt).</summary>
    public double Add(long bytes)
    {
        var now = _clock();
        _samples.Enqueue((now, bytes));
        while (_samples.Count > 2 && now - _samples.Peek().Time > _window)
            _samples.Dequeue();

        var (firstTime, firstBytes) = _samples.Peek();
        var seconds = (now - firstTime).TotalSeconds;
        return seconds > 0.05 ? (bytes - firstBytes) / seconds : 0;
    }

    private static Func<TimeSpan> StopwatchClock()
    {
        var watch = Stopwatch.StartNew();
        return () => watch.Elapsed;
    }
}
