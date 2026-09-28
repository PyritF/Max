using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Max.Ui;

namespace Max.Tools;

/// <summary>Datum und Uhrzeit jetzt – der System-Prompt kennt nur die vom Gesprächsbeginn.</summary>
internal sealed class ClockTool(Func<DateTime> clock) : ITool
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public string Name => "uhrzeit";
    public string? Argument => null;
    public string Description => "Datum und Uhrzeit genau jetzt.";
    public string Describe(string argument) => "Schaue auf die Uhr";

    public Task<string> RunAsync(string argument, CancellationToken ct)
    {
        var now = clock();
        return Task.FromResult($"{now.ToString("dddd, d. MMMM yyyy", German)}, {now:HH:mm:ss} Uhr (Kalenderwoche {ISOWeek.GetWeekOfYear(now)})");
    }
}

/// <summary>Was in diesem Rechner steckt: System, Prozessor, Speicher, Grafikkarte, Laufwerke.</summary>
internal sealed class SystemInfoTool : ITool
{
    public string Name => "system";
    public string? Argument => null;
    public string Description => "Betriebssystem, Prozessor, Arbeitsspeicher, Grafikkarte, Laufwerke mit freiem Platz, Laufzeit seit dem Start.";
    public string Describe(string argument) => "Sehe mir den Rechner an";

    public Task<string> RunAsync(string argument, CancellationToken ct)
    {
        var system = SystemSnapshot.Capture();
        var text = new StringBuilder();
        text.Append("Betriebssystem: ").Append(system.OsName).Append(" (").Append(RuntimeInformation.OSDescription.Trim()).Append(")\n");
        text.Append("Prozessor: ").Append(CpuName() ?? "unbekannt").Append(", ").Append(system.CpuCores).Append(" Kerne\n");
        text.Append("Arbeitsspeicher: ").Append(Format.Memory(system.TotalMemoryBytes));
        if (AvailableMemory() is { } free)
            text.Append(", davon frei: ").Append(Format.Memory(free));
        text.Append('\n');
        text.Append("Grafikkarte: ").Append(system.Hardware.Gpu is { } gpu ? $"{gpu.Name}, {Format.Memory(gpu.VramBytes)}" : "keine erkannt").Append('\n');
        text.Append("Läuft seit: ").Append(Uptime(TimeSpan.FromMilliseconds(Environment.TickCount64))).Append('\n');
        text.Append("Laufwerke:\n");
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.TotalSize < 1_000_000_000 || drive.DriveType is DriveType.Ram or DriveType.Unknown)
                    continue;
                text.Append("- ").Append(drive.Name).Append(": ").Append(Format.Gigabytes(drive.AvailableFreeSpace)).Append(" GB frei von ")
                    .Append(Format.Gigabytes(drive.TotalSize)).Append(" GB\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return Task.FromResult(text.ToString().TrimEnd());
    }

    internal static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} Tagen, {t.Hours} Stunden" : $"{t.Hours} Stunden, {t.Minutes} Minuten";

    private static string? CpuName()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
                return File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim();
            if (OperatingSystem.IsWindows())
                return Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
        }
        catch
        {
        }
        return null;
    }

    private static long? AvailableMemory()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
            {
                var line = File.ReadLines("/proc/meminfo").FirstOrDefault(l => l.StartsWith("MemAvailable:", StringComparison.Ordinal));
                if (line is not null && long.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], out var kb))
                    return kb * 1024;
            }
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (GlobalMemoryStatusEx(ref status))
                    return (long)status.AvailablePhysical;
            }
        }
        catch
        {
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
