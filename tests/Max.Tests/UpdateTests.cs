using System.Net;
using System.Security.Cryptography;
using System.Text;
using Max.Setup;
using Max.Ui;
using Max.Update;

namespace Max.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("0.10.0", "0.9.1", true)]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("v1.0.0", "0.9.9", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.0.9", "0.1.0", false)]
    [InlineData("kaputt", "0.1.0", false)]
    [InlineData(null, "0.1.0", false)]
    public void IsNewer(string? candidate, string current, bool expected) =>
        Assert.Equal(expected, AppVersion.IsNewer(candidate, current));

    [Fact]
    public void UnderTests_ThereIsNoExecutableToReplace() => Assert.Null(AppVersion.ExecutablePath);
}

public sealed class UpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "max-update-" + Guid.NewGuid().ToString("N"));
    private readonly MaxPaths _paths;
    private readonly byte[] _newExe = RandomNumberGenerator.GetBytes(200_000);
    private readonly string _sha;
    private readonly List<Uri> _requests = [];

    public UpdaterTests()
    {
        _paths = new MaxPaths(_dir);
        _paths.EnsureExists();
        _sha = Convert.ToHexStringLower(SHA256.HashData(_newExe));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static Manifest Manifest(string version, string? sha, int modelRevision = 1, string? minVersion = null, bool disabled = false, string? message = null) =>
        new(new AppInfo(version, minVersion, disabled, message, new()
            {
                [AppVersion.AssetKey] = new AppAsset(AppVersion.AssetKey == "win-x64" ? "max.exe" : "max", sha),
            }),
            new ModelEntry(modelRevision, "https://hf.example/resolve/main/Neu.gguf", null, 10, 4096));

    private HttpClient Http(byte[] body) => new(new FakeHandler(req =>
    {
        _requests.Add(req.RequestUri!);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    }));

    private string FakeExe()
    {
        var exe = Path.Combine(_dir, "max-test.exe");
        File.WriteAllText(exe, "alte Version");
        return exe;
    }

    [Fact]
    public void AppUpdate_OnlyForNewerVersions_WithChecksum()
    {
        Assert.NotNull(Updater.AppUpdate(Manifest("0.2.0", "abc"), "0.1.0"));
        Assert.Null(Updater.AppUpdate(Manifest("0.1.0", "abc"), "0.1.0"));
        Assert.Null(Updater.AppUpdate(Manifest("0.2.0", null), "0.1.0"));     // ohne Prüfsumme nichts laden
    }

    [Fact]
    public async Task NewVersion_IsDownloaded_AndSwappedWithTheRunningExe()
    {
        var exe = FakeExe();
        using var http = Http(_newExe);
        var updater = new Updater(http, _paths, exe, "0.1.0");

        await updater.RunAsync(Manifest("0.2.0", _sha), CancellationToken.None);

        Assert.Equal(_newExe, File.ReadAllBytes(exe));
        Assert.Equal("alte Version", File.ReadAllText(exe + ".old"));
        Assert.EndsWith("/v0.2.0/" + Path.GetFileName(AppVersion.AssetKey == "win-x64" ? "max.exe" : "max"), _requests.Single().AbsoluteUri);
        Assert.Contains("0.2.0 bereit", updater.Status);

        // Nächster Start: die alte Exe wird aufgeräumt.
        UpdateApplier.Apply(_paths, exe);
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public async Task WrongChecksum_LeavesTheRunningExeAlone()
    {
        var exe = FakeExe();
        using var http = Http(_newExe);
        var updater = new Updater(http, _paths, exe, "0.1.0");

        await updater.RunAsync(Manifest("0.2.0", new string('0', 64)), CancellationToken.None);

        Assert.Equal("alte Version", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".old"));
        Assert.Contains("nicht geklappt", updater.Status);
    }

    [Fact]
    public async Task WithoutExecutable_NoAppUpdate()
    {
        using var http = Http(_newExe);
        var updater = new Updater(http, _paths, exePath: null, "0.1.0");
        await updater.RunAsync(Manifest("0.2.0", _sha), CancellationToken.None);
        Assert.Empty(_requests);
        Assert.Equal("aktuell", updater.Status);
    }

    [Fact]
    public async Task Offline_SaysSo()
    {
        using var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("offline")));
        var updater = new Updater(http, _paths, null, "0.1.0");
        await updater.RunAsync(null, CancellationToken.None);
        Assert.Contains("offline", updater.Status);
    }

    [Fact]
    public async Task NewModel_IsReady_AndActiveAfterTheNextStart()
    {
        var model = Encoding.UTF8.GetBytes("neues Modell!");
        File.WriteAllText(_paths.Model, "altes Modell");
        File.WriteAllText(_paths.State, """{ "model": "Alt.gguf", "revision": 1, "sha256": "x", "sizeBytes": 12, "installedAt": "2026-09-27T12:00:00" }""");
        var manifest = Manifest("0.1.0", null, modelRevision: 2) with
        {
            Model = new ModelEntry(2, "https://hf.example/resolve/main/Neu.gguf", Convert.ToHexStringLower(SHA256.HashData(model)), model.Length, 4096),
        };
        using var http = Http(model);

        await new Updater(http, _paths, null, "0.1.0").RunAsync(manifest, CancellationToken.None);

        Assert.Equal("altes Modell", File.ReadAllText(_paths.Model));      // läuft ja noch
        Assert.True(File.Exists(_paths.ModelNext));
        Assert.Null(Updater.ModelUpdate(manifest, InstallState.Load(_paths), InstallState.LoadNext(_paths)));  // nicht doppelt laden

        Assert.True(UpdateApplier.Apply(_paths, null));
        Assert.Equal("neues Modell!", File.ReadAllText(_paths.Model));
        Assert.Equal("Neu.gguf", InstallState.Load(_paths)!.Model);
        Assert.Equal(2, InstallState.Load(_paths)!.Revision);
    }

    [Fact]
    public async Task StartupCheck_Disabled_StopsMax_WithTheMessage()
    {
        using var http = Http(_newExe);
        var e = await Assert.ThrowsAsync<SetupException>(() => StartupUpdate.CheckAsync(
            Manifest("0.1.0", null, disabled: true, message: "Wartung bis morgen."), new Updater(http, _paths, null, "0.1.0"), "0.1.0", null, new StepProgress(), CancellationToken.None));
        Assert.Equal("Wartung bis morgen.", e.Message);
    }

    [Fact]
    public async Task StartupCheck_TooOld_InstallsTheUpdate_ThenAsksForARestart()
    {
        var exe = FakeExe();
        using var http = Http(_newExe);
        var progress = new StepProgress();
        var e = await Assert.ThrowsAsync<SetupException>(() => StartupUpdate.CheckAsync(
            Manifest("0.3.0", _sha, minVersion: "0.2.0"), new Updater(http, _paths, exe, "0.1.0"), "0.1.0", exe, progress, CancellationToken.None));

        Assert.Contains("0.3.0", e.Message);
        Assert.Equal(_newExe, File.ReadAllBytes(exe));
        Assert.True(progress.Read().HasProgress);                             // sichtbarer Fortschritt
    }

    [Fact]
    public async Task StartupCheck_ReportsANewVersion_OrUpToDate()
    {
        using var http = Http(_newExe);
        var updater = new Updater(http, _paths, "x", "0.1.0");
        Assert.Equal("Version 0.2.0 kommt", await StartupUpdate.CheckAsync(Manifest("0.2.0", _sha), updater, "0.1.0", "x", new StepProgress(), CancellationToken.None));
        Assert.Equal("aktuell", await StartupUpdate.CheckAsync(Manifest("0.1.0", _sha), updater, "0.1.0", "x", new StepProgress(), CancellationToken.None));
        Assert.Empty(_requests);                                               // geladen wird im Hintergrund, nicht hier
    }
}
