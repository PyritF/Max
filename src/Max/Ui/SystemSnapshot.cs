using System.Runtime.InteropServices;
using Max.Setup;

namespace Max.Ui;

/// <summary>Momentaufnahme des Systems für den Startbildschirm.</summary>
internal sealed record SystemSnapshot(
    DateTime Now,
    string UserName,
    string OsName,
    int CpuCores,
    HardwareInfo Hardware,
    string WorkingDirectory)
{
    public static SystemSnapshot Capture() => new(
        Now: DateTime.Now,
        UserName: Environment.UserName,
        OsName: GetOsName(),
        CpuCores: Environment.ProcessorCount,
        Hardware: HardwareInfo.Detect(),
        WorkingDirectory: Environment.CurrentDirectory);

    public long TotalMemoryBytes => Hardware.RamBytes;

    /// <summary>Kurzbeschreibung, z. B. "Windows 11 · 16 Kerne · 32 GB".</summary>
    public string Summary => $"{OsName} · {CpuCores} Kerne · {Format.Memory(TotalMemoryBytes)}";

    private static string GetOsName()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows 11 meldet sich intern immer noch als "10.0" – erst die Build-Nummer verrät es.
            var build = Environment.OSVersion.Version.Build;
            return build >= 22000 ? "Windows 11" : "Windows 10";
        }

        if (OperatingSystem.IsLinux())
            return "Linux";

        if (OperatingSystem.IsMacOS())
            return "macOS";

        return RuntimeInformation.OSDescription;
    }
}
