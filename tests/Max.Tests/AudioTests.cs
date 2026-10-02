using Max.Llm;
using Max.Tools;

namespace Max.Tests;

/// <summary>Aufnahmen lesen (WAV, MP3, Ogg Opus/Vorbis, M4A) und das Werkzeug audio mit einer gespielten Spracherkennung.</summary>
public sealed class AudioTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("max-audio-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Eine Datei aus dem Repo (tests/…).</summary>
    internal static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Max.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, relative);
    }

    private static double Rms(float[] samples) => Math.Sqrt(samples.Sum(s => (double)s * s) / samples.Length);

    /// <summary>Schwingungen je Sekunde, gezählt nur, wo wirklich Ton ist (Stille am Rand zählt nicht mit).</summary>
    private static double Frequency(float[] samples)
    {
        int crossings = 0, first = -1, last = 0;
        var low = false;
        for (var i = 0; i < samples.Length; i++)
        {
            if (samples[i] < -0.05f)
                low = true;
            else if (samples[i] > 0.05f && low)
            {
                low = false;
                crossings++;
                if (first < 0)
                    first = i;
                last = i;
            }
        }
        return (crossings - 1) / ((last - first) / (double)AudioDecoder.SampleRate);
    }

    private string Wav(string name, int rate, int channels, Func<double, double> signal, double seconds)
    {
        var path = Path.Combine(_dir, name);
        var frames = (int)(rate * seconds);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * channels * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * channels * 2);
        for (var i = 0; i < frames; i++)
            for (var c = 0; c < channels; c++)
                writer.Write((short)(signal(i / (double)rate) * 32767 * 0.5));
        return path;
    }

    [Fact]
    public void Wav_StereoAt48k_BecomesMonoAt16k()
    {
        var path = Wav("ton.wav", 48_000, 2, t => Math.Sin(2 * Math.PI * 1000 * t), 2);

        var (samples, cut) = AudioDecoder.Load(path, 60, CancellationToken.None);

        Assert.False(cut);
        Assert.InRange(samples.Length, 31_900, 32_100);
        Assert.InRange(Rms(samples), 0.33, 0.38);                  // 0,5 · 1/√2
        Assert.InRange(Frequency(samples), 990, 1010);
    }

    [Fact]
    public void HighTones_DoNotFoldBackAsNoise()
    {
        // 12 kHz liegt über dem, was 16 kHz darstellen können (8 kHz) – ohne Filter käme er als 4 kHz zurück.
        var path = Wav("hoch.wav", 48_000, 1, t => Math.Sin(2 * Math.PI * 12_000 * t), 1);
        Assert.True(Rms(AudioDecoder.Load(path, 60, CancellationToken.None).Samples) < 0.02);
    }

    [Fact]
    public void LongRecordings_AreCut()
    {
        var path = Wav("lang.wav", 16_000, 1, t => Math.Sin(2 * Math.PI * 300 * t), 5);
        var (samples, cut) = AudioDecoder.Load(path, 2, CancellationToken.None);
        Assert.True(cut);
        Assert.Equal(32_000, samples.Length);
    }

    [Theory]
    [InlineData("tests/ton.mp3", 1.0)]
    [InlineData("tests/ton.ogg", 1.0)]
    [InlineData("tests/sprachnachricht.opus", 11.8)]
    public void CompressedFormats(string file, double seconds)
    {
        var (samples, _) = AudioDecoder.Load(RepoFile(file), 60, CancellationToken.None);
        Assert.InRange(samples.Length / (double)AudioDecoder.SampleRate, seconds - 0.15, seconds + 0.15);
        Assert.True(Rms(samples) > 0.05);
        if (file.StartsWith("tests/ton", StringComparison.Ordinal))
            Assert.InRange(Frequency(samples), 430, 450);
    }

    [Fact]
    public void M4a_ViaMediaFoundationOrFfmpeg()
    {
        // Unter Windows liest Media Foundation die Datei, anderswo nur ein installiertes ffmpeg.
        if (!OperatingSystem.IsWindows() && !Ffmpeg())
            return;
        var (samples, _) = AudioDecoder.Load(RepoFile("tests/sprachnachricht.m4a"), 60, CancellationToken.None);
        Assert.InRange(samples.Length / (double)AudioDecoder.SampleRate, 11.6, 12.1);
        Assert.True(Rms(samples) > 0.05);
    }

    private static bool Ffmpeg()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private sealed class FakeHearing : IHearing
    {
        public int Calls { get; private set; }

        public Task<Transcript> TranscribeAsync(float[] samples, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new Transcript("de",
            [
                new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(3), "Hallo, hier ist die Fahrradwerkstatt."),
                new TranscriptSegment(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9), "Du kannst das Rad am Donnerstag ab 16 Uhr abholen."),
                new TranscriptSegment(TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(70), "Die Reparatur kostet 82 Euro."),
            ]));
        }
    }

    [Fact]
    public async Task AudioTool_WritesDownWhatIsSaid_WithTimes()
    {
        var hearing = new FakeHearing();
        var tool = new AudioTool(() => hearing, () => _dir);
        var path = RepoFile("tests/sprachnachricht.opus");

        var result = await tool.RunAsync(path, CancellationToken.None);
        await tool.RunAsync(path + " | Euro", CancellationToken.None);

        Assert.StartsWith($"Aufnahme {path} (0:11 min, Sprache: Deutsch, ", result);
        Assert.Contains(") – Abschrift, 3 Zeilen:", result);
        Assert.Contains("[0:00] Hallo, hier ist die Fahrradwerkstatt.\n[0:03] Du kannst das Rad am Donnerstag ab 16 Uhr abholen.\n[1:05] Die Reparatur kostet 82 Euro.", result);
        Assert.Equal(1, hearing.Calls);                 // das zweite Mal aus dem Speicher
        Assert.Equal("Höre mir sprachnachricht.opus an", tool.Describe(path + " | Wann?"));
    }

    [Fact]
    public async Task AudioTool_ExplainsWhatIsWrong()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "kaputt.mp3"), "kein mp3");
        await File.WriteAllTextAsync(Path.Combine(_dir, "notiz.txt"), "x");
        var tool = new AudioTool(() => new FakeHearing(), () => _dir);

        Assert.Contains("gibt es nicht", await tool.RunAsync("fehlt.mp3", CancellationToken.None));
        Assert.Contains("keine Aufnahme", await tool.RunAsync("notiz.txt", CancellationToken.None));
        Assert.Contains("ließ sich nicht abspielen", await tool.RunAsync("kaputt.mp3", CancellationToken.None));
        Assert.Contains("noch nicht auf diesem Rechner", await new AudioTool(() => null, () => _dir).RunAsync(RepoFile("tests/ton.mp3"), CancellationToken.None));
        Assert.Contains("dafür gibt es \"audio\"", await new ReadFileTool(() => _dir).RunAsync(RepoFile("tests/ton.mp3"), CancellationToken.None));
    }

    [Fact]
    public void DraggedRecording_IsListenedTo()
    {
        var path = RepoFile("tests/sprachnachricht.opus");
        Assert.Equal(new ToolCall("audio", $"{path} | Wann kann ich das Rad abholen?"),
            Assert.Single(Attachments.Calls($"\"{path}\" Wann kann ich das Rad abholen?", _dir)));
    }
}
