using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Max.Tools;

/// <summary>
/// Liest Aufnahmen als Mono-Samples mit 16 kHz – so will die Spracherkennung sie. WAV, MP3 und Ogg (Opus, wie bei
/// Sprachnachrichten, und Vorbis) kann Max selbst; M4A, AAC, MP4, WMA und FLAC unter Windows über Media Foundation
/// (ist dabei), sonst über ffmpeg, falls installiert.
/// </summary>
internal static class AudioDecoder
{
    public const int SampleRate = 16_000;

    public static readonly string[] Extensions =
        [".wav", ".mp3", ".ogg", ".oga", ".opus", ".m4a", ".aac", ".mp4", ".flac", ".wma", ".webm", ".3gp", ".amr", ".mka"];

    public static bool IsAudio(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Die Aufnahme als 16-kHz-Mono – höchstens <paramref name="maxSeconds"/> lang.</summary>
    /// <returns>Die Samples und ob die Aufnahme länger war (dann fehlt der Rest).</returns>
    public static (float[] Samples, bool Cut) Load(string path, int maxSeconds, CancellationToken ct)
    {
        var output = new Resampler(maxSeconds * SampleRate);
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".wav":
                Wav(path, output, ct);
                break;
            case ".mp3":
                Mp3(path, output, ct);
                break;
            case ".ogg" or ".oga" or ".opus":
                Ogg(path, output, ct);
                break;
            default:
                if (OperatingSystem.IsWindows() && MediaFoundation.TryDecode(path, output, ct))
                    break;
                if (!Ffmpeg(path, maxSeconds, output, ct))
                    throw new InvalidOperationException(OperatingSystem.IsWindows()
                        ? $"{Path.GetFileName(path)} kann ich nicht öffnen – als MP3, WAV oder M4A gespeichert geht es."
                        : $"Für {Path.GetExtension(path)}-Dateien brauche ich ffmpeg (Paket ffmpeg) – oder die Aufnahme als MP3, WAV oder Ogg.");
                break;
        }
        return (output.ToArray(), output.Full);
    }

    // ── WAV ──

    private static void Wav(string path, Resampler output, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
            throw new InvalidDataException("keine WAV-Datei");
        reader.ReadInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
            throw new InvalidDataException("keine WAV-Datei");

        int format = 0, channels = 0, rate = 0, bits = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadUInt32();
            var next = stream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format == 0xFFFE && size >= 40)
                {
                    reader.ReadBytes(8);
                    format = reader.ReadUInt16();       // die ersten zwei Bytes der Unterformat-GUID: 1 = PCM, 3 = Float
                }
            }
            else if (id == "data")
            {
                if (channels == 0 || rate == 0 || format is not (1 or 3) || bits is not (8 or 16 or 24 or 32))
                    throw new InvalidDataException($"WAV-Format {format} mit {bits} Bit kann ich nicht lesen");
                output.Start(rate, channels);
                var bytesPerSample = bits / 8;
                var frame = bytesPerSample * channels;
                var buffer = new byte[frame * 8192];
                var samples = new float[channels * 8192];
                var left = Math.Min(size, stream.Length - stream.Position);
                while (left > 0 && !output.Full)
                {
                    ct.ThrowIfCancellationRequested();
                    var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left) / frame * frame);
                    if (read <= 0)
                        break;
                    left -= read;
                    var count = read / bytesPerSample;
                    for (var i = 0; i < count; i++)
                        samples[i] = Sample(buffer, i * bytesPerSample, bits, format);
                    output.Push(samples, count);
                }
                return;
            }
            if (next > stream.Length)
                break;
            stream.Position = next;
        }
        throw new InvalidDataException("WAV-Datei ohne Ton");
    }

    private static float Sample(byte[] data, int offset, int bits, int format) => (bits, format) switch
    {
        (8, _) => (data[offset] - 128) / 128f,
        (16, _) => BitConverter.ToInt16(data, offset) / 32768f,
        (24, _) => ((data[offset] | data[offset + 1] << 8 | (sbyte)data[offset + 2] << 16)) / 8388608f,
        (32, 3) => BitConverter.ToSingle(data, offset),
        _ => BitConverter.ToInt32(data, offset) / 2147483648f,
    };

    // ── MP3 (NLayer) ──

    private static void Mp3(string path, Resampler output, CancellationToken ct)
    {
        using var mp3 = new NLayer.MpegFile(path);
        output.Start(mp3.SampleRate, mp3.Channels);
        var buffer = new float[mp3.Channels * 8192];
        while (!output.Full && mp3.ReadSamples(buffer, 0, buffer.Length) is var read and > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Push(buffer, read);
        }
    }

    // ── Ogg: Opus (Concentus) oder Vorbis (NVorbis) ──

    private static void Ogg(string path, Resampler output, CancellationToken ct)
    {
        var head = new byte[512];
        using (var probe = File.OpenRead(path))
            Array.Resize(ref head, probe.Read(head, 0, head.Length));
        var text = Encoding.ASCII.GetString(head);
        var opus = text.IndexOf("OpusHead", StringComparison.Ordinal);
        if (opus >= 0 && opus + 9 < head.Length)
        {
            var channels = Math.Max(1, (int)head[opus + 9]);
            Concentus.OpusCodecFactory.AttemptToUseNativeLibrary = false;
            var decoder = Concentus.OpusCodecFactory.CreateDecoder(48_000, channels, null);
            using var stream = File.OpenRead(path);
            var reader = new Concentus.Oggfile.OpusOggReadStream(decoder, stream);
            output.Start(48_000, channels);
            var buffer = Array.Empty<float>();
            while (!output.Full && reader.HasNextPacket)
            {
                ct.ThrowIfCancellationRequested();
                var packet = reader.DecodeNextPacket();
                if (packet is null || packet.Length == 0)
                    continue;
                if (buffer.Length < packet.Length)
                    buffer = new float[packet.Length];
                for (var i = 0; i < packet.Length; i++)
                    buffer[i] = packet[i] / 32768f;
                output.Push(buffer, packet.Length);
            }
            return;
        }
        if (text.Contains("vorbis", StringComparison.Ordinal))
        {
            using var vorbis = new NVorbis.VorbisReader(path);
            output.Start(vorbis.SampleRate, vorbis.Channels);
            var buffer = new float[vorbis.Channels * 8192];
            while (!output.Full && vorbis.ReadSamples(buffer, 0, buffer.Length) is var read and > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Push(buffer, read);
            }
            return;
        }
        throw new InvalidDataException("Ogg-Datei weder mit Opus noch mit Vorbis");
    }

    // ── ffmpeg (falls installiert) ──

    private static bool Ffmpeg(string path, int maxSeconds, Resampler output, CancellationToken ct)
    {
        Process? process;
        try
        {
            var start = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", path, "-t", maxSeconds.ToString(),
                         "-f", "f32le", "-ac", "1", "-ar", SampleRate.ToString(), "pipe:1" })
                start.ArgumentList.Add(argument);
            process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;               // nicht installiert
        }
        if (process is null)
            return false;
        using (process)
        {
            _ = process.StandardError.ReadToEndAsync(ct);
            output.Start(SampleRate, 1);
            var stream = process.StandardOutput.BaseStream;
            var bytes = new byte[4 * 16384];
            var samples = new float[16384];
            var carry = 0;
            while (stream.Read(bytes, carry, bytes.Length - carry) is var read and > 0)
            {
                ct.ThrowIfCancellationRequested();
                var total = carry + read;
                var count = total / 4;
                Buffer.BlockCopy(bytes, 0, samples, 0, count * 4);
                output.Push(samples, count);
                carry = total - count * 4;
                Array.Copy(bytes, count * 4, bytes, 0, carry);
            }
            process.WaitForExit();
            return process.ExitCode == 0 && output.Count > 0;
        }
    }

    /// <summary>
    /// Mischt auf Mono und rechnet auf 16 kHz um – Stück für Stück, damit eine lange Aufnahme nicht erst ganz im
    /// Speicher liegen muss. Gefiltert wird mit einem gefensterten Sinc, damit hohe Töne nicht als Rauschen in die
    /// Sprache zurückfallen.
    /// </summary>
    internal sealed class Resampler(int maxSamples)
    {
        private const int Taps = 16;                    // je Seite
        private const int TableSteps = 256;             // Auflösung der Filtertabelle zwischen zwei Samples

        private readonly List<float> _output = [];
        private readonly List<float> _input = [];       // Mono-Eingang, noch nicht ganz verbraucht
        private double _position;                       // nächste Ausgabe, in Eingangs-Samples ab _input[0]
        private double _step = 1;
        private int _channels = 1;
        private float[] _table = [];
        private double _cutoff = 1;

        public bool Full => _output.Count >= maxSamples;
        public int Count => _output.Count;

        public void Start(int rate, int channels)
        {
            _channels = Math.Max(1, channels);
            _step = (double)rate / SampleRate;
            _cutoff = Math.Min(1.0, 1.0 / _step) * 0.95;
            _table = new float[(Taps + 1) * TableSteps + 1];
            for (var i = 0; i < _table.Length; i++)
            {
                var x = (double)i / TableSteps;                             // Abstand in Eingangs-Samples
                var sinc = x == 0 ? 1 : Math.Sin(Math.PI * x * _cutoff) / (Math.PI * x * _cutoff);
                var window = x >= Taps ? 0 : 0.5 + 0.5 * Math.Cos(Math.PI * x / Taps);
                _table[i] = (float)(sinc * window * _cutoff);
            }
        }

        public void Push(float[] interleaved, int count)
        {
            for (var i = 0; i + _channels <= count; i += _channels)
            {
                var sum = 0f;
                for (var c = 0; c < _channels; c++)
                    sum += interleaved[i + c];
                _input.Add(sum / _channels);
            }
            Produce(final: false);
        }

        public float[] ToArray()
        {
            Produce(final: true);
            return _output.Count > maxSamples ? _output.GetRange(0, maxSamples).ToArray() : _output.ToArray();
        }

        private void Produce(bool final)
        {
            if (_table.Length == 0)
                return;
            while (!Full && (final ? _position < _input.Count : _position + Taps + 1 < _input.Count))
            {
                var center = (int)Math.Floor(_position);
                var fraction = _position - center;
                double sum = 0;
                for (var k = -Taps; k <= Taps; k++)
                {
                    var index = center + k;
                    if (index < 0 || index >= _input.Count)
                        continue;
                    var distance = Math.Abs(k - fraction) * TableSteps;
                    var slot = (int)distance;
                    if (slot + 1 >= _table.Length)
                        continue;
                    sum += _input[index] * (_table[slot] + (_table[slot + 1] - _table[slot]) * (distance - slot));
                }
                _output.Add((float)sum);
                _position += _step;
            }
            // Verbrauchtes vorn abschneiden (genug für das Filter bleibt stehen).
            var drop = (int)Math.Floor(_position) - Taps - 1;
            if (drop > 4096)
            {
                _input.RemoveRange(0, drop);
                _position -= drop;
            }
        }
    }

    /// <summary>
    /// Media Foundation (Windows): liest fast alles, was Windows selbst abspielen kann – M4A von der Sprachaufnahme,
    /// AAC, MP4, WMA, FLAC. Direkt über die COM-Tabellen, ohne zusätzliche Pakete.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static unsafe class MediaFoundation
    {
        private const uint Version = 0x00020070;          // MF_VERSION
        private const uint Lite = 1;                      // MFSTARTUP_LITE: ohne Netzwerk
        private const uint FirstAudioStream = 0xFFFFFFFD;
        private const uint AllStreams = 0xFFFFFFFE;
        private const uint EndOfStream = 0x2, TypeChanged = 0x20, Error = 0x1;

        private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid AudioType = new("73647561-0000-0010-8000-00AA00389B71");
        private static readonly Guid FloatFormat = new("00000003-0000-0010-8000-00AA00389B71");
        private static readonly Guid ChannelCount = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static readonly Guid SamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");

        public static bool TryDecode(string path, Resampler output, CancellationToken ct)
        {
            var com = CoInitializeEx(IntPtr.Zero, 0) >= 0;
            try
            {
                if (MFStartup(Version, Lite) < 0)
                    return false;
                try
                {
                    return Decode(path, output, ct);
                }
                finally
                {
                    MFShutdown();
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Llm.LlmEngine.Log($"Media Foundation: {e.Message}");
                return false;
            }
            finally
            {
                if (com)
                    CoUninitialize();
            }
        }

        private static bool Decode(string path, Resampler output, CancellationToken ct)
        {
            if (MFCreateSourceReaderFromURL(path, IntPtr.Zero, out var reader) < 0)
                return false;
            try
            {
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Slot(reader, 4))(reader, AllStreams, 0));
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Slot(reader, 4))(reader, FirstAudioStream, 1));
                Check(MFCreateMediaType(out var wanted));
                try
                {
                    SetGuid(wanted, MajorType, AudioType);
                    SetGuid(wanted, SubType, FloatFormat);
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Slot(reader, 7))(reader, FirstAudioStream, IntPtr.Zero, wanted));
                }
                finally
                {
                    Release(wanted);
                }
                StartWithCurrentType(reader, output);

                var samples = Array.Empty<float>();
                while (!output.Full)
                {
                    ct.ThrowIfCancellationRequested();
                    uint stream, flags;
                    long time;
                    IntPtr sample;
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Slot(reader, 9))(
                        reader, FirstAudioStream, 0, &stream, &flags, &time, &sample));
                    if ((flags & Error) != 0)
                        throw new InvalidDataException("Fehler beim Lesen");
                    if ((flags & TypeChanged) != 0)
                        StartWithCurrentType(reader, output);
                    if (sample != IntPtr.Zero)
                    {
                        try
                        {
                            IntPtr buffer;
                            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(sample, 41))(sample, &buffer));
                            try
                            {
                                byte* data;
                                uint max, length;
                                Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &length));
                                try
                                {
                                    var count = (int)(length / 4);
                                    if (samples.Length < count)
                                        samples = new float[count];
                                    Marshal.Copy((IntPtr)data, samples, 0, count);
                                    output.Push(samples, count);
                                }
                                finally
                                {
                                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer);
                                }
                            }
                            finally
                            {
                                Release(buffer);
                            }
                        }
                        finally
                        {
                            Release(sample);
                        }
                    }
                    if ((flags & EndOfStream) != 0)
                        break;
                }
                return output.Count > 0;
            }
            finally
            {
                Release(reader);
            }
        }

        private static void StartWithCurrentType(IntPtr reader, Resampler output)
        {
            IntPtr type;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(reader, 6))(reader, FirstAudioStream, &type));
            try
            {
                uint rate, channels;
                var key = SamplesPerSecond;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Slot(type, 7))(type, &key, &rate));
                key = ChannelCount;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Slot(type, 7))(type, &key, &channels));
                output.Start((int)rate, (int)channels);
            }
            finally
            {
                Release(type);
            }
        }

        private static void SetGuid(IntPtr attributes, Guid key, Guid value) =>
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(attributes, 24))(attributes, &key, &value));

        private static void* Slot(IntPtr instance, int index) => (*(void***)instance)[index];

        private static void Release(IntPtr instance)
        {
            if (instance != IntPtr.Zero)
                ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, 2))(instance);
        }

        private static void Check(int result)
        {
            if (result < 0)
                throw new InvalidDataException($"Media Foundation meldet 0x{result:X8}");
        }

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint mode);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        [DllImport("mfplat.dll")]
        private static extern int MFStartup(uint version, uint flags);

        [DllImport("mfplat.dll")]
        private static extern int MFShutdown();

        [DllImport("mfplat.dll")]
        private static extern int MFCreateMediaType(out IntPtr mediaType);

        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);
    }
}
