using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Max.Setup;

internal enum GpuVendor { Nvidia, Amd, Intel, Other }

internal sealed record GpuInfo(string Name, long VramBytes, GpuVendor Vendor);

/// <summary>Was Max über die Hardware wissen muss, um die passende Stufe zu wählen.</summary>
internal sealed record HardwareInfo(long RamBytes, GpuInfo? Gpu)
{
    /// <summary>Grafikkarten mit weniger Speicher (meist integrierte Grafik) zählen nicht.</summary>
    internal const long MinUsefulVramBytes = 2L * 1024 * 1024 * 1024;

    public long VramBytes => Gpu?.VramBytes ?? 0;

    public static HardwareInfo Detect() => new(
        RamBytes: GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        Gpu: DetectGpu());

    private static GpuInfo? DetectGpu()
    {
        var gpus = new List<GpuInfo>();
        try { gpus.AddRange(QueryNvidiaSmi()); } catch { /* kein NVIDIA-Treiber – kein Problem */ }
        if (gpus.Count == 0 && OperatingSystem.IsWindows())
        {
            try { gpus.AddRange(QueryWindowsRegistry()); } catch { /* keine Rechte o. Ä. – dann eben CPU */ }
        }

        return gpus.Where(g => g.VramBytes >= MinUsefulVramBytes).MaxBy(g => g.VramBytes);
    }

    private static IEnumerable<GpuInfo> QueryNvidiaSmi()
    {
        var start = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start);
        if (process is null)
            return [];

        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(2000) || !output.Wait(500))
        {
            try { process.Kill(); } catch { }
            return [];
        }
        return process.ExitCode == 0 ? ParseNvidiaSmi(output.Result) : [];
    }

    /// <summary>Liest Zeilen wie "NVIDIA GeForce RTX 4070, 12282" (Speicher in MiB).</summary>
    internal static IReadOnlyList<GpuInfo> ParseNvidiaSmi(string output)
    {
        var result = new List<GpuInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var comma = line.LastIndexOf(',');
            if (comma <= 0)
                continue;
            var name = line[..comma].Trim();
            if (name.Length == 0 || !long.TryParse(line[(comma + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib) || mib <= 0)
                continue;
            result.Add(new GpuInfo(name, mib * 1024 * 1024, GpuVendor.Nvidia));
        }
        return result;
    }

    /// <summary>
    /// Alle Grafikkarten unter Windows (auch AMD und Intel) stehen in der Registry.
    /// WMI (Win32_VideoController.AdapterRAM) taugt nicht: Es schneidet bei 4 GB ab.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<GpuInfo> QueryWindowsRegistry()
    {
        const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        using var root = Registry.LocalMachine.OpenSubKey(displayClass);
        if (root is null)
            yield break;

        foreach (var subName in root.GetSubKeyNames())
        {
            if (!int.TryParse(subName, out _))
                continue; // nur 0000, 0001, … – "Properties" & Co. überspringen

            using var sub = root.OpenSubKey(subName);
            var name = sub?.GetValue("DriverDesc") as string;
            var vram = ReadRegistrySize(sub?.GetValue("HardwareInformation.qwMemorySize"))
                       ?? ReadRegistrySize(sub?.GetValue("HardwareInformation.MemorySize"));
            if (name is null || vram is null)
                continue;
            yield return new GpuInfo(name, vram.Value, VendorFromName(name));
        }
    }

    /// <summary>Die Treiber speichern die Größe mal als Zahl, mal als Bytefolge.</summary>
    internal static long? ReadRegistrySize(object? value) => value switch
    {
        long l => l,
        int i => (uint)i,
        byte[] { Length: >= 8 } b => BitConverter.ToInt64(b, 0),
        byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0),
        _ => null,
    };

    internal static GpuVendor VendorFromName(string name) =>
        name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Nvidia
        : name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Amd
        : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) || name.Contains("Arc", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Intel
        : GpuVendor.Other;
}
