using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Max.Setup;

/// <summary>
/// Wer vor dem Rechner sitzt: der volle Name des Kontos (unter Windows z. B. aus dem Microsoft-Konto),
/// nicht nur der kurze Benutzername. Bleibt lokal – steht nur im Speicher und im Prompt.
/// </summary>
internal sealed record UserIdentity(string UserName, string FullName)
{
    /// <summary>Erstes Wort des vollen Namens – so spricht Max den Nutzer an.</summary>
    public string FirstName => FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? UserName;

    public static UserIdentity Detect()
    {
        var user = Environment.UserName;
        string? full = null;
        try
        {
            if (OperatingSystem.IsWindows())
                full = WindowsDisplayName() ?? WindowsFullName(user);
            else if (OperatingSystem.IsLinux() && File.Exists("/etc/passwd"))
                full = FromPasswd(File.ReadLines("/etc/passwd"), user);
        }
        catch
        {
            // Name nicht ermittelbar – dann eben der Benutzername.
        }
        return Create(user, full);
    }

    internal static UserIdentity Create(string userName, string? fullName)
    {
        var cleaned = fullName?.Trim();
        return new UserIdentity(userName, string.IsNullOrWhiteSpace(cleaned) ? userName : cleaned);
    }

    /// <summary>GECOS-Feld aus /etc/passwd: "alex:x:1000:1000:Alex Beispiel,,,:/home/alex:/bin/bash" → "Alex Beispiel".</summary>
    internal static string? FromPasswd(IEnumerable<string> lines, string user)
    {
        foreach (var line in lines)
        {
            var fields = line.Split(':');
            if (fields.Length >= 5 && fields[0] == user)
                return fields[4].Split(',')[0];
        }
        return null;
    }

    // ── Windows ──

    private const int NameDisplay = 3;

    /// <summary>Anzeigename – klappt vor allem bei Domänen-Konten.</summary>
    [SupportedOSPlatform("windows")]
    private static string? WindowsDisplayName()
    {
        var size = 256;
        var buffer = new StringBuilder(size);
        return GetUserNameExW(NameDisplay, buffer, ref size) ? buffer.ToString() : null;
    }

    /// <summary>"Vollständiger Name" des lokalen Kontos – bei Microsoft-Konten der Name aus dem Konto.</summary>
    [SupportedOSPlatform("windows")]
    private static string? WindowsFullName(string user)
    {
        if (NetUserGetInfo(null, user, 10, out var buffer) != 0 || buffer == IntPtr.Zero)
            return null;
        try
        {
            var info = Marshal.PtrToStructure<UserInfo10>(buffer);
            return Marshal.PtrToStringUni(info.FullName);
        }
        finally
        {
            NetApiBufferFree(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo10
    {
        public IntPtr Name;
        public IntPtr Comment;
        public IntPtr UserComment;
        public IntPtr FullName;
    }

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserNameExW(int nameFormat, StringBuilder buffer, ref int size);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buffer);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
