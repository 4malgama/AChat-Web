using System.Buffers.Binary;
using System.Text;

namespace AChatWeb.Protocol;

public static class PacketWriter
{
    private static readonly Encoding Utf16 = new UnicodeEncoding(true, false, true);

    public static byte[] UpdateProfile(string json) => String16Packet(501, json);
    public static byte[] RequestAvatar() => String16Packet(1000, "");
    public static byte[] RequestChats() => Empty(1002);

    public static byte[] RequestMessages(ulong chatId)
    {
        ValidateId(chatId);
        byte[] data = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(data, 1003);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(2), chatId);
        return data;
    }

    public static byte[] DownloadFile(ulong fileId, ulong requestId)
    {
        ValidateId(fileId);
        ValidateId(requestId);
        byte[] data = new byte[18];
        BinaryPrimitives.WriteUInt16BigEndian(data, 2702);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(2), fileId);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(10), requestId);
        return data;
    }

    private static void ValidateId(ulong id)
    {
        if (id == 0 || id > long.MaxValue) throw new ArgumentOutOfRangeException(nameof(id));
    }

    private static byte[] String16Packet(ushort id, string text)
    {
        if (text.Length > short.MaxValue) throw new ArgumentException("String is too long for Java readShort().");
        byte[] data = new byte[4 + text.Length * 2];
        BinaryPrimitives.WriteUInt16BigEndian(data, id);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), (ushort)text.Length);
        Utf16.GetBytes(text, data.AsSpan(4));
        return data;
    }

    public static byte[] UpdateAvatar(byte[] image)
    {
        if (image.Length is 0 or > AChatWeb.Services.AvatarImage.MaximumUploadBytes)
            throw new ArgumentException("Invalid avatar size.");
        string text = Convert.ToBase64String(image);
        byte[] data = new byte[6 + text.Length * 2];
        BinaryPrimitives.WriteUInt16BigEndian(data, 1001);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(2), text.Length);
        Utf16.GetBytes(text, data.AsSpan(6));
        return data;
    }

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
