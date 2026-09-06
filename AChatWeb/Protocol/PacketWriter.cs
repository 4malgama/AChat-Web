using System.Buffers.Binary;
using System.Text;

namespace AChatWeb.Protocol;

public static class PacketWriter
{
    private static readonly Encoding Utf16 = new UnicodeEncoding(true, false, true);

    public static byte[] Empty(ushort id)
    {
        byte[] data = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(data, id);
        return data;
    }

    public static byte[] Location(string locale)
    {
        byte[] text = Utf16.GetBytes(locale);
        byte[] data = new byte[4 + text.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data, 301);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), checked((ushort)locale.Length));
        text.CopyTo(data, 4);
        return data;
    }

    public static byte[] Credentials(bool registration, string login, string password)
    {
        // The server uses readShort(), so these lengths must fit a signed 16-bit value.
        if (login.Length is < 1 or > short.MaxValue || password.Length is < 1 or > short.MaxValue)
            throw new ArgumentException("Invalid credentials length.");

        byte[] loginBytes = Utf16.GetBytes(login);
        byte[] passwordBytes = Utf16.GetBytes(password);
        int header = registration ? 6 : 7;
        byte[] data = new byte[header + loginBytes.Length + passwordBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data, registration ? (ushort)103 : (ushort)100);
        int offset = 2;
        if (!registration) data[offset++] = 0; // remember=false: no persistent token in this release.
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), (ushort)login.Length);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 2), (ushort)password.Length);
        loginBytes.CopyTo(data, header);
        passwordBytes.CopyTo(data, header + loginBytes.Length);
        Array.Clear(passwordBytes);
        return data;
    }
}
