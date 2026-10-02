using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Max.Tools;

/// <summary>Was gerade in der Zwischenablage liegt: ein Bild (Screenshot), Text oder kopierte Dateien.</summary>
/// <param name="Image">Bilddaten als Datei-Inhalt (PNG oder BMP) – null, wenn kein Bild darin ist.</param>
internal sealed record ClipboardContent(byte[]? Image, string ImageExtension, string? Text, IReadOnlyList<string> Files)
{
    public static readonly ClipboardContent Empty = new(null, "", null, []);

    public bool IsEmpty => Image is null && string.IsNullOrEmpty(Text) && Files.Count == 0;
}

/// <summary>Liest die Zwischenablage – nur lesen, nie schreiben.</summary>
internal interface IClipboard
{
    /// <summary>Der Inhalt – oder eine Ausnahme mit einem Satz, warum es nicht geht.</summary>
    ClipboardContent Read();
}

/// <summary>
/// Die echte Zwischenablage: unter Windows direkt über user32 (PNG, Bitmap, Dateien, Text), unter Linux über
/// <c>wl-paste</c> (Wayland) oder <c>xclip</c> (X11).
/// </summary>
internal sealed class SystemClipboard : IClipboard
{
    public ClipboardContent Read()
    {
        if (OperatingSystem.IsWindows())
            return Windows.Read();
        if (OperatingSystem.IsLinux())
            return Linux.Read();
        throw new InvalidOperationException("Die Zwischenablage kann ich auf diesem System nicht lesen.");
    }

    /// <summary>Eine Windows-Bitmap aus der Zwischenablage (DIB, ohne Dateikopf) → BMP-Datei.</summary>
    internal static byte[] DibToBmp(byte[] dib)
    {
        if (dib.Length < 40)
            throw new InvalidDataException("Bild in der Zwischenablage ist unvollständig.");
        var headerSize = BitConverter.ToInt32(dib, 0);
        var bitCount = BitConverter.ToUInt16(dib, 14);
        var compression = BitConverter.ToUInt32(dib, 16);
        var colorsUsed = BitConverter.ToUInt32(dib, 32);
        var palette = colorsUsed != 0 ? (int)colorsUsed : bitCount <= 8 ? 1 << bitCount : 0;
        // Bei BI_BITFIELDS (3) folgen einem einfachen Kopf (40 Bytes) noch drei Farbmasken.
        var masks = headerSize == 40 && compression == 3 ? 12 : 0;
        var offset = 14 + headerSize + masks + palette * 4;
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(offset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    private static class Windows
    {
        private const uint UnicodeText = 13, Dib = 8, DibV5 = 17, Drop = 15;

        public static ClipboardContent Read()
        {
            // Hält gerade ein anderes Programm die Zwischenablage, kurz warten.
            for (var attempt = 0; !OpenClipboard(IntPtr.Zero); attempt++)
            {
                if (attempt >= 10)
                    throw new InvalidOperationException("Die Zwischenablage ist gerade von einem anderen Programm belegt.");
                Thread.Sleep(50);
            }
            try
            {
                if (IsClipboardFormatAvailable(Drop) && Files() is { Count: > 0 } files)
                    return ClipboardContent.Empty with { Files = files };
                var png = RegisterClipboardFormat("PNG");
                if (png != 0 && IsClipboardFormatAvailable(png) && Bytes(png) is { } pngBytes)
                    return ClipboardContent.Empty with { Image = pngBytes, ImageExtension = ".png" };
                foreach (var format in new[] { DibV5, Dib })
                    if (IsClipboardFormatAvailable(format) && Bytes(format) is { } dib)
                        return ClipboardContent.Empty with { Image = DibToBmp(dib), ImageExtension = ".bmp" };
                if (IsClipboardFormatAvailable(UnicodeText) && Bytes(UnicodeText) is { } text)
                    return ClipboardContent.Empty with { Text = Encoding.Unicode.GetString(text).TrimEnd('\0') };
                return ClipboardContent.Empty;
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static byte[]? Bytes(uint format)
        {
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero)
                return null;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return null;
            try
            {
                var size = (int)Math.Min((ulong)GlobalSize(handle), 64UL * 1024 * 1024);
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, size);
                return bytes;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }

        private static List<string> Files()
        {
            var drop = GetClipboardData(Drop);
            var files = new List<string>();
            if (drop == IntPtr.Zero)
                return files;
            var count = DragQueryFile(drop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count && i < 20; i++)
            {
                var length = DragQueryFile(drop, i, null, 0);
                var name = new StringBuilder((int)length + 1);
                DragQueryFile(drop, i, name, length + 1);
                files.Add(name.ToString());
            }
            return files;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterClipboardFormat(string name);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern UIntPtr GlobalSize(IntPtr memory);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? file, uint length);
    }

    private static class Linux
    {
        public static ClipboardContent Read()
        {
            if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 } && Run("wl-paste", "--list-types") is { } waylandTypes)
                return Content(waylandTypes, type => Run("wl-paste", $"--no-newline --type {type}"));
            if (Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } && Run("xclip", "-selection clipboard -t TARGETS -o") is { } x11Types)
                return Content(x11Types, type => Run("xclip", $"-selection clipboard -t {type} -o"));
            if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not { Length: > 0 } && Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 })
                throw new InvalidOperationException("Hier gibt es keine Zwischenablage (keine grafische Oberfläche).");
            throw new InvalidOperationException("Für die Zwischenablage brauche ich unter Linux wl-paste (Paket wl-clipboard) oder xclip.");
        }

        private static ClipboardContent Content(byte[] typeList, Func<string, byte[]?> get)
        {
            var types = Encoding.UTF8.GetString(typeList).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (types.Contains("text/uri-list") && get("text/uri-list") is { } uris)
            {
                var files = Encoding.UTF8.GetString(uris).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(u => u.StartsWith("file://", StringComparison.Ordinal))
                    .Select(u => new Uri(u).LocalPath).ToList();
                if (files.Count > 0)
                    return ClipboardContent.Empty with { Files = files };
            }
            if (types.Contains("image/png") && get("image/png") is { Length: > 0 } png)
                return ClipboardContent.Empty with { Image = png, ImageExtension = ".png" };
            var textType = types.FirstOrDefault(t => t is "text/plain;charset=utf-8" or "UTF8_STRING") ?? types.FirstOrDefault(t => t.StartsWith("text/plain", StringComparison.Ordinal));
            if (textType is not null && get(textType) is { } text)
                return ClipboardContent.Empty with { Text = Encoding.UTF8.GetString(text) };
            return ClipboardContent.Empty;
        }

        private static byte[]? Run(string program, string arguments)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(program, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (process is null)
                    return null;
                using var output = new MemoryStream();
                var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
                if (!process.WaitForExit(5000) || !copy.Wait(5000))
                {
                    process.Kill();
                    return null;
                }
                return process.ExitCode == 0 ? output.ToArray() : null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;            // Programm nicht installiert
            }
        }
    }
}
