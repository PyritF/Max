using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Max.Setup;

internal enum GpuVendor { Nvidia, Amd, Intel, Other }

internal sealed record GpuInfo(string Name, long VramBytes, GpuVendor Vendor);

/// <summary>Was Max über die Hardware wissen muss, um zu prüfen, ob der Rechner reicht.</summary>
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
        if (gpus.Count == 0 && OperatingSystem.IsLinux())
        {
            // AMD meldet seinen Grafikspeicher im sysfs; für den Rest (Intel Arc …) fragt Max Vulkan selbst.
            try { gpus.AddRange(QueryLinuxSysfs("/sys/class/drm")); } catch { /* kein sysfs – dann eben CPU */ }
            if (gpus.Count == 0)
            {
                try { gpus.AddRange(QueryVulkanInfo()); } catch { /* vulkaninfo nicht installiert */ }
            }
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

    /// <summary>
    /// AMD-Karten unter Linux (Treiber amdgpu): Der Grafikspeicher steht in
    /// <c>/sys/class/drm/cardN/device/mem_info_vram_total</c>, der Name in <c>product_name</c> oder in der PCI-Liste.
    /// </summary>
    internal static IEnumerable<GpuInfo> QueryLinuxSysfs(string drm, IEnumerable<string>? pciIdFiles = null)
    {
        if (!Directory.Exists(drm))
            yield break;
        foreach (var card in Directory.EnumerateDirectories(drm, "card*").OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(card);
            if (name.Length <= 4 || !name[4..].All(char.IsDigit))
                continue;                                   // card0-HDMI-A-1 & Co. sind Anschlüsse
            var device = Path.Combine(card, "device");
            if (Read(Path.Combine(device, "vendor")) != "0x1002"
                || !long.TryParse(Read(Path.Combine(device, "mem_info_vram_total")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var vram))
                continue;
            var product = Read(Path.Combine(device, "product_name"));
            var label = product is { Length: > 0 } ? product
                : PciName("1002", Read(Path.Combine(device, "device"))?.Replace("0x", ""), pciIdFiles) is { } pci ? "AMD " + pci
                : "AMD Radeon";
            yield return new GpuInfo(label, vram, GpuVendor.Amd);
        }

        static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static readonly string[] PciIdFiles = ["/usr/share/hwdata/pci.ids", "/usr/share/misc/pci.ids", "/usr/share/pci.ids"];

    /// <summary>Der Name eines PCI-Geräts aus der PCI-Liste des Systems – oder null.</summary>
    internal static string? PciName(string vendor, string? device, IEnumerable<string>? files = null)
    {
        if (device is null)
            return null;
        foreach (var file in files ?? PciIdFiles)
        {
            if (!File.Exists(file))
                continue;
            var inVendor = false;
            foreach (var line in File.ReadLines(file))
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;
                if (line[0] != '\t')
                {
                    if (inVendor)
                        return null;
                    inVendor = line.StartsWith(vendor + "  ", StringComparison.OrdinalIgnoreCase);
                }
                else if (inVendor && line.Length > 1 && line[1] != '\t' && line[1..].StartsWith(device + "  ", StringComparison.OrdinalIgnoreCase))
                {
                    return line[(device.Length + 3)..].Trim();
                }
            }
        }
        return null;
    }

    private static IEnumerable<GpuInfo> QueryVulkanInfo()
    {
        var start = new ProcessStartInfo("vulkaninfo")
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
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(4000) || !output.Wait(500))
        {
            try { process.Kill(); } catch { }
            return [];
        }
        return ParseVulkanInfo(output.Result);
    }

    /// <summary>
    /// Die Ausgabe von <c>vulkaninfo</c>: je Grafikkarte (GPU0:, GPU1: …) Name, Art und die Speicherbereiche.
    /// Nur eigenständige Karten zählen; ihr Grafikspeicher ist der größte Bereich mit DEVICE_LOCAL.
    /// </summary>
    internal static IReadOnlyList<GpuInfo> ParseVulkanInfo(string output)
    {
        var result = new List<GpuInfo>();
        var sections = System.Text.RegularExpressions.Regex.Split(output.ReplaceLineEndings("\n"), @"^GPU\d+:[ \t]*$", System.Text.RegularExpressions.RegexOptions.Multiline);
        foreach (var section in sections.Length > 1 ? sections.Skip(1) : sections)
        {
            var name = System.Text.RegularExpressions.Regex.Match(section, @"deviceName\s*=\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline);
            var type = System.Text.RegularExpressions.Regex.Match(section, @"deviceType\s*=\s*(\S+)");
            if (!name.Success || !type.Success || !type.Groups[1].Value.Contains("DISCRETE_GPU", StringComparison.Ordinal))
                continue;
            long vram = 0;
            var heaps = System.Text.RegularExpressions.Regex.Split(section, @"memoryHeaps\[\d+\]:");
            foreach (var heap in heaps.Skip(1))
            {
                var size = System.Text.RegularExpressions.Regex.Match(heap, @"size\s*=\s*(\d+)");
                var block = heap.Split("memoryTypes", 2)[0];
                if (size.Success && block.Contains("DEVICE_LOCAL", StringComparison.Ordinal) && long.TryParse(size.Groups[1].Value, out var bytes))
                    vram = Math.Max(vram, bytes);
            }
            if (vram > 0)
            {
                var label = name.Groups[1].Value.Trim();
                result.Add(new GpuInfo(label, vram, VendorFromName(label)));
            }
        }
        return result;
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
