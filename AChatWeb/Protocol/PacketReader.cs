using System.Buffers.Binary;
using System.Text;

namespace AChatWeb.Protocol;

/// <summary>Java/Netty: Big Endian numbers, UTF-16BE strings. Lengths count UTF-16 code units.</summary>
public sealed class PacketReader(ReadOnlyMemory<byte> bytes)
{
    private int offset;
    private static readonly Encoding Utf16 = new UnicodeEncoding(true, false, true);
    public int Remaining => bytes.Length - offset;
    public byte[] Rest(int maximumBytes)
    {
        if (Remaining > maximumBytes) throw new InvalidDataException("Payload exceeds the limit.");
        return Take(Remaining).ToArray();
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
            throw new InvalidDataException("Truncated packet or invalid field length.");
        var span = bytes.Span.Slice(offset, count);
        offset += count;
        return span;
    }

    public ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    public ulong UInt64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));
    public uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
    public byte[] Bytes32(int maximumBytes)
    {
        uint count = UInt32();
        if (count > maximumBytes) throw new InvalidDataException("File exceeds the limit.");
        return Take(checked((int)count)).ToArray();
    }

    public string String32(int maxChars = 32 * 1024 * 1024)
    {
        uint count = UInt32();
        if (count > maxChars || count > (uint)(Remaining / 2))
            throw new InvalidDataException("Invalid long string length.");
        return Utf16.GetString(Take(checked((int)count * 2)));
    }
    public bool Boolean() => Take(1)[0] switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException("Invalid boolean.")
    };

    public string String16(int maxChars = ushort.MaxValue)
    {
        int count = UInt16();
        if (count > maxChars) throw new InvalidDataException("String exceeds the limit.");
        return Utf16.GetString(Take(checked(count * 2)));
    }

    public void SkipString32()
    {
        uint count = BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        if (count > (uint)(Remaining / 2)) throw new InvalidDataException("Invalid long string length.");
        Take(checked((int)count * 2));
    }

    public void Finish()
    {
        if (Remaining != 0) throw new InvalidDataException("Unexpected trailing packet data.");
    }
}
