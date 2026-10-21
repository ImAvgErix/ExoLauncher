namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Protobuf's wire format, as much as Discord's settings protos need (<see cref="DiscordFavoriteGifs"/>,
/// <see cref="DiscordUserSettings"/>): walk a message's fields, and write tags, varints and
/// length-delimited values back. A field Exo doesn't know passes through as its raw bytes.
/// </summary>
internal static class DiscordProto
{
    /// <summary>The fields of a message in order: number, wire type and the value's bytes (a varint's own bytes for wire type 0).</summary>
    internal static IEnumerable<(int Number, int Wire, byte[] Value)> Fields(byte[] data)
    {
        var at = 0;
        while (at < data.Length)
        {
            var tag = ReadVarint(data, ref at);
            var number = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            byte[] value;
            switch (wire)
            {
                case 0:
                    var start = at;
                    ReadVarint(data, ref at);
                    value = data[start..at];
                    break;
                case 1: value = Take(data, ref at, 8); break;
                case 2:
                    var length = ReadVarint(data, ref at);
                    if (length > (ulong)(data.Length - at)) throw new InvalidDataException("Truncated field.");
                    value = Take(data, ref at, (int)length);
                    break;
                case 5: value = Take(data, ref at, 4); break;
                default: throw new InvalidDataException("Unsupported wire type.");
            }
            yield return (number, wire, value);
        }
    }

    private static byte[] Take(byte[] data, ref int at, int count)
    {
        if (count > data.Length - at) throw new InvalidDataException("Truncated field.");
        var value = data[at..(at + count)];
        at += count;
        return value;
    }

    private static ulong ReadVarint(byte[] data, ref int at)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (at >= data.Length) throw new InvalidDataException("Truncated varint.");
            var b = data[at++];
            result |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("Varint too long.");
    }

    /// <summary>A wire-type-0 value's number.</summary>
    internal static ulong Varint(byte[] value) { var at = 0; return ReadVarint(value, ref at); }

    /// <summary>Writes a field back exactly as it was read.</summary>
    internal static void Raw(List<byte> output, int number, int wire, byte[] value)
    {
        WriteTag(output, number, wire);
        if (wire == 2) WriteVarint(output, (ulong)value.Length);
        output.AddRange(value);
    }

    internal static void WriteTag(List<byte> output, int number, int wire) => WriteVarint(output, (ulong)((number << 3) | wire));

    internal static void WriteString(List<byte> output, int number, string text) =>
        WriteBytes(output, number, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>A length-delimited field: an embedded message, a string or packed numbers.</summary>
    internal static void WriteBytes(List<byte> output, int number, IReadOnlyCollection<byte> bytes)
    {
        WriteTag(output, number, 2);
        WriteVarint(output, (ulong)bytes.Count);
        output.AddRange(bytes);
    }

    internal static void WriteVarint(List<byte> output, ulong value)
    {
        while (value >= 0x80) { output.Add((byte)(value | 0x80)); value >>= 7; }
        output.Add((byte)value);
    }
}
