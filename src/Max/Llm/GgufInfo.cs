using System.Text;

namespace Max.Llm;

/// <summary>
/// Liest die paar Angaben aus dem Kopf einer GGUF-Datei, die Max vor dem Laden braucht –
/// vor allem die Zahl der Schichten, um zu entscheiden, wie viele davon auf die Grafikkarte passen.
/// </summary>
internal sealed record GgufInfo(string Architecture, int BlockCount, int TrainedContext)
{
    private const uint Magic = 0x46554747; // "GGUF" (little endian)

    private enum ValueType : uint
    {
        UInt8, Int8, UInt16, Int16, UInt32, Int32, Float32, Bool, String, Array, UInt64, Int64, Float64,
    }

    public static GgufInfo Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Read(stream);
    }

    public static GgufInfo Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Keine GGUF-Datei.");
        var version = reader.ReadUInt32();
        if (version < 2)
            throw new InvalidDataException($"GGUF-Version {version} wird nicht unterstützt.");

        reader.ReadUInt64(); // Anzahl der Tensoren – hier egal
        var kvCount = reader.ReadUInt64();

        var architecture = "";
        long? blockCount = null, context = null;
        for (ulong i = 0; i < kvCount; i++)
        {
            var key = ReadString(reader);
            var type = (ValueType)reader.ReadUInt32();

            if (key == "general.architecture" && type == ValueType.String)
                architecture = ReadString(reader);
            else if (architecture.Length > 0 && key == $"{architecture}.block_count")
                blockCount = ReadInteger(reader, type);
            else if (architecture.Length > 0 && key == $"{architecture}.context_length")
                context = ReadInteger(reader, type);
            else
                Skip(reader, type);

            if (blockCount is not null && context is not null)
                break;
        }

        if (architecture.Length == 0 || blockCount is null)
            throw new InvalidDataException("GGUF-Kopf unvollständig.");
        return new GgufInfo(architecture, (int)blockCount, (int)(context ?? 0));
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > int.MaxValue)
            throw new InvalidDataException("GGUF-Zeichenkette zu lang.");
        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    private static long ReadInteger(BinaryReader reader, ValueType type) => type switch
    {
        ValueType.UInt8 => reader.ReadByte(),
        ValueType.Int8 => reader.ReadSByte(),
        ValueType.UInt16 => reader.ReadUInt16(),
        ValueType.Int16 => reader.ReadInt16(),
        ValueType.UInt32 => reader.ReadUInt32(),
        ValueType.Int32 => reader.ReadInt32(),
        ValueType.UInt64 => (long)reader.ReadUInt64(),
        ValueType.Int64 => reader.ReadInt64(),
        _ => throw new InvalidDataException($"Erwartete Zahl, gefunden: {type}."),
    };

    private static void Skip(BinaryReader reader, ValueType type)
    {
        switch (type)
        {
            case ValueType.String:
                var length = reader.ReadUInt64();
                reader.BaseStream.Seek((long)length, SeekOrigin.Current);
                break;
            case ValueType.Array:
                var itemType = (ValueType)reader.ReadUInt32();
                var count = reader.ReadUInt64();
                if (FixedSize(itemType) is { } size)
                    reader.BaseStream.Seek((long)count * size, SeekOrigin.Current);
                else
                    for (ulong i = 0; i < count; i++)
                        Skip(reader, itemType); // z. B. die Liste aller Tokens (Zeichenketten)
                break;
            default:
                reader.BaseStream.Seek(FixedSize(type) ?? throw new InvalidDataException($"Unbekannter GGUF-Typ {type}."), SeekOrigin.Current);
                break;
        }
    }

    private static int? FixedSize(ValueType type) => type switch
    {
        ValueType.UInt8 or ValueType.Int8 or ValueType.Bool => 1,
        ValueType.UInt16 or ValueType.Int16 => 2,
        ValueType.UInt32 or ValueType.Int32 or ValueType.Float32 => 4,
        ValueType.UInt64 or ValueType.Int64 or ValueType.Float64 => 8,
        _ => null,
    };
}
