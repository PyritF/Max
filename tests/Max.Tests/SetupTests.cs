using System.Net;
using System.Security.Cryptography;
using System.Text;
using Max.Setup;
using Max.Ui;

namespace Max.Tests;

public class MaxPathsTests
{
    private static string? NoEnv(string _) => null;

    [Fact]
    public void MaxHome_WinsOverEverything()
    {
        var root = MaxPaths.ResolveRoot(n => n == "MAX_HOME" ? "/tmp/maxhome" : null, isWindows: true, @"C:\Local", @"C:\Users\alex");
        Assert.Equal(Path.GetFullPath("/tmp/maxhome"), root);
    }

    [Fact]
    public void Windows_UsesLocalAppData() =>
        Assert.Equal(Path.Combine(@"C:\Local", "Max"), MaxPaths.ResolveRoot(NoEnv, isWindows: true, @"C:\Local", @"C:\Users\alex"));

    [Fact]
    public void Linux_UsesXdgDataHome_OrLocalShare()
    {
        Assert.Equal(Path.Combine("/data", "max"), MaxPaths.ResolveRoot(n => n == "XDG_DATA_HOME" ? "/data" : null, false, "", "/home/alex"));
        Assert.Equal(Path.Combine("/home/alex", ".local", "share", "max"), MaxPaths.ResolveRoot(NoEnv, false, "", "/home/alex"));
    }

    [Fact]
    public void FileNames_AreNeutral()
    {
        var paths = new MaxPaths("/x");
        Assert.Equal(Path.Combine("/x", "core.bin"), paths.Model);
        Assert.Equal(Path.Combine("/x", "core.bin.part"), paths.ModelPart);
        Assert.Equal(Path.Combine("/x", "state.json"), paths.State);
    }
}

public class HardwareInfoTests
{
    [Fact]
    public void ParseNvidiaSmi_ReadsNameAndMemory()
    {
        var gpus = HardwareInfo.ParseNvidiaSmi("NVIDIA GeForce RTX 4070, 12282\n");
        var gpu = Assert.Single(gpus);
        Assert.Equal("NVIDIA GeForce RTX 4070", gpu.Name);
        Assert.Equal(12282L * 1024 * 1024, gpu.VramBytes);
        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
    }

    [Fact]
    public void ParseNvidiaSmi_HandlesSeveralGpus_BlankLines_AndGarbage()
    {
        var output = "\r\nNVIDIA A100, 81920\r\n\r\nfoo\nbar, baz\n, 100\nTesla T4, 15360\n";
        var gpus = HardwareInfo.ParseNvidiaSmi(output);
        Assert.Equal(["NVIDIA A100", "Tesla T4"], gpus.Select(g => g.Name));
    }

    [Fact]
    public void ReadRegistrySize_Numbers()
    {
        Assert.Equal(8L << 30, HardwareInfo.ReadRegistrySize(8L << 30));  // REG_QWORD
        Assert.Equal(4_294_967_295L, HardwareInfo.ReadRegistrySize(-1));   // REG_DWORD ist vorzeichenlos
        Assert.Null(HardwareInfo.ReadRegistrySize("8 GB"));
    }

    [Fact]
    public void ReadRegistrySize_Bytes() =>
        Assert.Equal(12L << 30, HardwareInfo.ReadRegistrySize(BitConverter.GetBytes(12L << 30)));

    [Theory]
    [InlineData("AMD Radeon RX 7800 XT", "Amd")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "Intel")]
    [InlineData("NVIDIA GeForce RTX 3060", "Nvidia")]
    [InlineData("Microsoft Basic Display Adapter", "Other")]
    public void VendorFromName(string name, string vendor) =>
        Assert.Equal(Enum.Parse<GpuVendor>(vendor), HardwareInfo.VendorFromName(name));
}

public class TierSelectorTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static HardwareInfo Hw(double ramGb, double vramGb = 0) =>
        new((long)(ramGb * GiB), vramGb > 0 ? new GpuInfo("GPU", (long)(vramGb * GiB), GpuVendor.Nvidia) : null);

    [Theory]
    [InlineData(4, 0, "S")]
    [InlineData(7.8, 0, "M")]     // "8 GB" meldet sich oft etwas kleiner
    [InlineData(16, 0, "M")]
    [InlineData(32, 0, "M")]      // nur CPU: großes Modell wäre zu langsam
    [InlineData(47.8, 0, "XL")]
    [InlineData(4, 6, "M")]
    [InlineData(16, 8, "L")]
    [InlineData(16, 12, "L")]
    [InlineData(16, 15.99, "XL")] // 16-GB-Karte meldet 16376 MiB
    [InlineData(32, 24, "XL")]
    public void Select_FollowsTable(double ram, double vram, string expected) =>
        Assert.Equal(Enum.Parse<Tier>(expected), TierSelector.Select(Hw(ram, vram)));

    [Fact]
    public void Resolve_Override_WinsOverHardware()
    {
        Assert.Equal(Tier.S, TierSelector.Resolve(Hw(64, 24), "s"));
        Assert.Equal(Tier.XL, TierSelector.Resolve(Hw(4), " XL "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("XXL")]
    [InlineData("7")]
    public void Resolve_InvalidOverride_IsIgnored(string value) =>
        Assert.Equal(Tier.M, TierSelector.Resolve(Hw(16), value));
}

public class ManifestTests
{
    [Fact]
    public void Embedded_IsCompleteAndSane()
    {
        var manifest = ManifestSource.Embedded();
        Assert.True(manifest.IsComplete);
        foreach (var tier in Enum.GetValues<Tier>())
        {
            var entry = manifest.For(tier)!;
            Assert.StartsWith("https://", entry.Url);
            Assert.True(entry.SizeBytes > 0);
            Assert.True(entry.ContextSize >= 4096);
            Assert.True(entry.Revision >= 1);
        }
    }

    [Fact]
    public async Task Load_UsesRemote_WhenValid()
    {
        var json = """
            { "app": { "version": "9.9.9" },
              "tiers": { "S": { "revision": 2, "url": "https://a", "sha256": null, "sizeBytes": 1, "contextSize": 4096 },
                         "M": { "revision": 2, "url": "https://b", "sha256": null, "sizeBytes": 1, "contextSize": 4096 },
                         "L": { "revision": 2, "url": "https://c", "sha256": null, "sizeBytes": 1, "contextSize": 4096 },
                         "XL": { "revision": 2, "url": "https://d", "sha256": null, "sizeBytes": 1, "contextSize": 4096 } } }
            """;
        using var http = new HttpClient(new FakeHandler(_ => Text(HttpStatusCode.OK, json)));
        var manifest = await ManifestSource.LoadAsync(http, CancellationToken.None, "https://example/manifest.json");
        Assert.Equal("9.9.9", manifest.App.Version);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{ kaputt")]
    [InlineData(HttpStatusCode.OK, """{ "app": { "version": "1" }, "tiers": {} }""")]
    [InlineData(HttpStatusCode.NotFound, "")]
    public async Task Load_FallsBackToEmbedded(HttpStatusCode status, string body)
    {
        using var http = new HttpClient(new FakeHandler(_ => Text(status, body)));
        var manifest = await ManifestSource.LoadAsync(http, CancellationToken.None, "https://example/manifest.json");
        Assert.Equal(ManifestSource.Embedded().App.Version, manifest.App.Version);
        Assert.True(manifest.IsComplete);
    }

    [Fact]
    public async Task Load_FallsBackToEmbedded_WhenOffline()
    {
        using var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("offline")));
        var manifest = await ManifestSource.LoadAsync(http, CancellationToken.None, "https://example/manifest.json");
        Assert.True(manifest.IsComplete);
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public sealed class ModelDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "max-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MaxPaths _paths;
    private readonly byte[] _data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 123);
    private readonly string _sha;

    public ModelDownloaderTests()
    {
        _paths = new MaxPaths(_dir);
        _sha = Convert.ToHexStringLower(SHA256.HashData(_data));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private TierEntry Entry(string? sha = null) => new(1, "https://hf.example/resolve/main/model.gguf", sha, _data.Length, 4096);

    [Fact]
    public async Task Download_Complete_WithShaFromManifest()
    {
        using var http = new HttpClient(new FakeHandler(Serve()));
        var result = await new ModelDownloader(http).DownloadAsync(Entry(_sha), _paths, new StepProgress(), CancellationToken.None);

        Assert.Equal(_sha, result.Sha256);
        Assert.Equal(_data.Length, result.SizeBytes);
        Assert.Equal(_data, File.ReadAllBytes(_paths.ModelPart));
    }

    [Fact]
    public async Task Download_FollowsRedirect_AndTakesShaFromHuggingFaceHeader()
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new FakeHandler(req =>
        {
            requests.Add(req.RequestUri!);
            if (req.RequestUri!.Host == "hf.example")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://cdn.example/blob");
                redirect.Headers.TryAddWithoutValidation("X-Linked-ETag", $"\"{_sha}\"");
                redirect.Headers.TryAddWithoutValidation("X-Linked-Size", _data.Length.ToString());
                return redirect;
            }
            return Serve()(req);
        }));

        var progress = new StepProgress();
        var result = await new ModelDownloader(http).DownloadAsync(Entry(sha: null), _paths, progress, CancellationToken.None);

        Assert.Equal(_sha, result.Sha256);
        Assert.Equal(["hf.example", "cdn.example"], requests.Select(r => r.Host));
        var (has, done, total, _) = progress.Read();
        Assert.True(has);
        Assert.Equal(_data.Length, done);
        Assert.Equal(_data.Length, total);
    }

    [Fact]
    public async Task Download_ResumesWithRange()
    {
        Directory.CreateDirectory(_dir);
        const int already = 1_000_000;
        File.WriteAllBytes(_paths.ModelPart, _data[..already]);

        long? requestedFrom = null;
        using var http = new HttpClient(new FakeHandler(req =>
        {
            requestedFrom = req.Headers.Range?.Ranges.Single().From;
            return Serve()(req);
        }));
        var result = await new ModelDownloader(http).DownloadAsync(Entry(_sha), _paths, new StepProgress(), CancellationToken.None);

        Assert.Equal(already, requestedFrom);
        Assert.Equal(_sha, result.Sha256);
        Assert.Equal(_data, File.ReadAllBytes(_paths.ModelPart));
    }

    [Fact]
    public async Task Download_StartsOver_WhenServerIgnoresRange()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(_paths.ModelPart, new byte[500]); // falscher Inhalt – wird verworfen

        using var http = new HttpClient(new FakeHandler(Serve(supportRange: false)));
        var result = await new ModelDownloader(http).DownloadAsync(Entry(_sha), _paths, new StepProgress(), CancellationToken.None);

        Assert.Equal(_sha, result.Sha256);
        Assert.Equal(_data, File.ReadAllBytes(_paths.ModelPart));
    }

    [Fact]
    public async Task Download_StartsOver_OnRangeNotSatisfiable()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(_paths.ModelPart, new byte[_data.Length + 10]); // größer als die Datei

        using var http = new HttpClient(new FakeHandler(Serve()));
        var result = await new ModelDownloader(http).DownloadAsync(Entry(_sha), _paths, new StepProgress(), CancellationToken.None);

        Assert.Equal(_sha, result.Sha256);
    }

    [Fact]
    public async Task Download_WrongSha_DeletesPart()
    {
        using var http = new HttpClient(new FakeHandler(Serve()));
        var error = await Assert.ThrowsAsync<SetupException>(() =>
            new ModelDownloader(http).DownloadAsync(Entry(new string('a', 64)), _paths, new StepProgress(), CancellationToken.None));

        Assert.Contains("beschädigt", error.Message);
        Assert.False(File.Exists(_paths.ModelPart));
    }

    [Fact]
    public async Task Download_WithoutAnySha_Refuses()
    {
        using var http = new HttpClient(new FakeHandler(Serve()));
        await Assert.ThrowsAsync<SetupException>(() =>
            new ModelDownloader(http).DownloadAsync(Entry(sha: null), _paths, new StepProgress(), CancellationToken.None));
    }

    [Fact]
    public async Task Download_NotEnoughSpace()
    {
        using var http = new HttpClient(new FakeHandler(Serve()));
        var error = await Assert.ThrowsAsync<SetupException>(() =>
            new ModelDownloader(http, freeSpace: _ => 1000).DownloadAsync(Entry(_sha), _paths, new StepProgress(), CancellationToken.None));

        Assert.StartsWith("Zu wenig Speicherplatz", error.Message);
    }

    [Fact]
    public async Task Download_Offline_GivesFriendlyMessage()
    {
        using var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("no route")));
        var error = await Assert.ThrowsAsync<SetupException>(() =>
            new ModelDownloader(http).DownloadAsync(Entry(_sha), _paths, new StepProgress(), CancellationToken.None));

        Assert.StartsWith("Keine Verbindung", error.Message);
    }

    [Fact]
    public async Task Download_ConnectionDrops_KeepsPartForResume()
    {
        using var http = new HttpClient(new FakeHandler(req =>
        {
            var response = Serve()(req);
            response.Content = new StreamContent(new FailingStream(_data, failAfter: 2_000_000));
            response.Content.Headers.ContentLength = _data.Length;
            return response;
        }));

        var error = await Assert.ThrowsAsync<SetupException>(() =>
            new ModelDownloader(http).DownloadAsync(Entry(_sha), _paths, new StepProgress(), CancellationToken.None));

        Assert.Contains("weiter", error.Message);
        Assert.True(new FileInfo(_paths.ModelPart).Length > 0);
    }

    [Fact]
    public async Task Download_Cancelled_KeepsPart()
    {
        using var cts = new CancellationTokenSource();
        var progress = new StepProgress();
        using var http = new HttpClient(new FakeHandler(req =>
        {
            var response = Serve()(req);
            response.Content = new StreamContent(new FailingStream(_data, failAfter: int.MaxValue, onRead: pos => { if (pos > 1_500_000) cts.Cancel(); }));
            response.Content.Headers.ContentLength = _data.Length;
            return response;
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ModelDownloader(http).DownloadAsync(Entry(_sha), _paths, progress, cts.Token));

        Assert.True(File.Exists(_paths.ModelPart));
    }

    [Theory]
    [InlineData("\"ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789\"", "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("\"d41d8cd98f00b204e9800998ecf8427e\"", null)] // MD5-ETag – keine Prüfsumme für uns
    [InlineData(null, null)]
    public void NormalizeSha(string? input, string? expected) =>
        Assert.Equal(expected, ModelDownloader.NormalizeSha(input));

    /// <summary>Liefert <see cref="_data"/> – mit Range-Unterstützung wie ein echter Server.</summary>
    private Func<HttpRequestMessage, HttpResponseMessage> Serve(bool supportRange = true) => req =>
    {
        var from = req.Headers.Range?.Ranges.Single().From ?? 0;
        if (supportRange && from > 0)
        {
            if (from >= _data.Length)
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(_data[(int)from..]) };
            partial.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, _data.Length - 1, _data.Length);
            return partial;
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_data) };
    };

    /// <summary>Stream, der nach <c>failAfter</c> Bytes wie eine gerissene Verbindung eine IOException wirft.</summary>
    private sealed class FailingStream(byte[] data, int failAfter, Action<long>? onRead = null) : MemoryStream(data, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            onRead?.Invoke(Position);
            ct.ThrowIfCancellationRequested();
            if (Position >= failAfter)
                throw new IOException("connection reset");
            await Task.Yield();
            return await base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, 256 * 1024)], ct);
        }
    }
}

public sealed class InstallStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "max-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void NotInstalled_WhenNothingThere() =>
        Assert.False(InstallState.IsInstalled(new MaxPaths(_dir)));

    [Fact]
    public void Commit_MakesItInstalled()
    {
        var paths = new MaxPaths(_dir);
        paths.EnsureExists();
        File.WriteAllBytes(paths.ModelPart, new byte[42]);

        InstallState.Commit(paths, Tier.M, new TierEntry(3, "https://x", null, 42, 16384), new DownloadResult(new string('b', 64), 42), new DateTime(2026, 9, 25, 21, 0, 0));

        Assert.True(InstallState.IsInstalled(paths));
        Assert.False(File.Exists(paths.ModelPart));
        var state = InstallState.Load(paths)!;
        Assert.Equal("M", state.Tier);
        Assert.Equal(3, state.Revision);
        Assert.Equal(42, state.SizeBytes);
        Assert.Equal(16384, state.ContextSize);
    }

    [Fact]
    public void OldStateWithoutContextSize_UsesDefault()
    {
        var paths = new MaxPaths(_dir);
        paths.EnsureExists();
        File.WriteAllText(paths.State, """{ "tier": "S", "revision": 1, "sha256": "x", "sizeBytes": 1, "installedAt": "2026-09-25T21:00:00" }""");
        Assert.Equal(InstallState.DefaultContextSize, InstallState.Load(paths)!.ContextSize);
    }

    [Fact]
    public void NotInstalled_WhenSizeDoesNotMatch_OrStateIsBroken()
    {
        var paths = new MaxPaths(_dir);
        paths.EnsureExists();
        File.WriteAllBytes(paths.ModelPart, new byte[42]);
        InstallState.Commit(paths, Tier.S, new TierEntry(1, "https://x", null, 42, 8192), new DownloadResult(new string('b', 64), 42), DateTime.Now);

        File.WriteAllBytes(paths.Model, new byte[10]);
        Assert.False(InstallState.IsInstalled(paths));

        File.WriteAllText(paths.State, "{ kaputt");
        Assert.Null(InstallState.Load(paths));
    }
}

public sealed class ModelShelfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "max-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static void Install(MaxPaths paths, Tier tier, int size)
    {
        File.WriteAllBytes(paths.ModelPart, new byte[size]);
        InstallState.Commit(paths, tier, new TierEntry(1, "https://x", null, size, 8192), new DownloadResult(new string('b', 64), size), DateTime.Now);
    }

    [Fact]
    public void Gleiche_Stufe_bleibt_liegen()
    {
        var paths = new MaxPaths(_dir);
        paths.EnsureExists();
        Install(paths, Tier.L, 50);

        Assert.False(ModelShelf.Prepare(paths, Tier.L));
        Assert.True(InstallState.IsInstalled(paths));
    }

    [Fact]
    public void Neue_Stufe_legt_das_alte_Modell_beiseite_und_verlangt_die_Einrichtung()
    {
        var paths = new MaxPaths(_dir);
        paths.EnsureExists();
        Install(paths, Tier.L, 50);
        File.WriteAllBytes(paths.ModelPart, new byte[3]);

        Assert.True(ModelShelf.Prepare(paths, Tier.S));

        Assert.False(InstallState.IsInstalled(paths));
        Assert.False(File.Exists(paths.ModelPart));
        Assert.Equal(50, new FileInfo(ModelShelf.ModelFile(paths, "L")).Length);
        Assert.Equal(["L"], ModelShelf.Shelved(paths));
    }

    [Fact]
    public void Zurueckwechseln_holt_das_beiseitegelegte_Modell()
    {
        var paths = new MaxPaths(_dir);
        paths.EnsureExists();
        Install(paths, Tier.L, 50);
        ModelShelf.Prepare(paths, Tier.S);
        Install(paths, Tier.S, 20);

        ModelShelf.Prepare(paths, Tier.L);

        Assert.True(InstallState.IsInstalled(paths));
        Assert.Equal("L", InstallState.Load(paths)!.Tier);
        Assert.Equal(["S"], ModelShelf.Shelved(paths));
    }
}

/// <summary>Ersetzt das Netz in Tests: Jede Anfrage beantwortet die übergebene Funktion.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(respond(request));
}

public class UserIdentityTests
{
    [Fact]
    public void FirstName_IsFirstWordOfFullName() =>
        Assert.Equal("Alex", UserIdentity.Create("alex", "Alex Beispiel").FirstName);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyFullName_FallsBackToUserName(string? full)
    {
        var identity = UserIdentity.Create("alex", full);
        Assert.Equal("alex", identity.FullName);
        Assert.Equal("alex", identity.FirstName);
    }

    [Fact]
    public void Passwd_GecosField()
    {
        string[] lines =
        [
            "root:x:0:0:root:/root:/bin/bash",
            "alex:x:1000:1000:Alex Beispiel,,,:/home/alex:/bin/bash",
        ];
        Assert.Equal("Alex Beispiel", UserIdentity.FromPasswd(lines, "alex"));
        Assert.Null(UserIdentity.FromPasswd(lines, "niemand"));
    }
}
