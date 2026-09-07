using System.Globalization;
using System.Text.Json;

namespace AChatWeb.Services;

public sealed record ChatParticipant(ulong Id, string Name, string Post, string? AvatarDataUrl);
public sealed record ChatSummary(ulong Id, bool IsGroup, ChatParticipant? Participant)
{
    public string Name => Participant?.Name ?? (IsGroup ? $"Группа #{Id}" : $"Чат #{Id}");
}
public sealed record ChatAttachment(ulong Id, string Name, string Type, long? Size)
{
    public string SizeLabel => ChatPresentation.FileSize(Size);
}
public sealed record ChatMessage(ulong Id, string Text, long? UnixTime, ChatParticipant Sender,
    IReadOnlyList<ChatAttachment> Attachments);

public sealed class ChatHistory
{
    public IReadOnlyList<ChatMessage> Messages { get; internal set; } = [];
    public bool IsLoaded { get; internal set; }
    public bool IsLoading { get; internal set; }
    public string? Error { get; internal set; }
    public int Revision { get; internal set; }
}

public sealed record ChatListSnapshot(IReadOnlyList<ChatSummary> Chats, string? Error, int DownloadProtocol);
public sealed record HistorySnapshot(ulong ChatId, IReadOnlyList<ChatMessage> Messages,
    ChatParticipant? Participant, string? Error);
public sealed record DownloadPayload(ulong FileId, string Name, byte[] Bytes);

/// <summary>Server-filtered data only: absent fields clear old values, never infer permission.</summary>
public static class ChatData
{
    private static string Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? "" : "";

    private static ulong Id(JsonElement obj, string key, bool allowZero = false)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong id)
            && id <= long.MaxValue && (id > 0 || allowZero)) return id;
        throw new InvalidDataException("Invalid entity id.");
    }

    private static JsonElement Array(JsonElement obj, string key)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.Array) return value;
        throw new InvalidDataException("Missing array.");
    }

    public static ChatParticipant Participant(JsonElement obj, bool allowDeleted = false)
    {
        ulong id = Id(obj, "id", allowDeleted);
        string name = Text(obj, "display_name");
        if (string.IsNullOrWhiteSpace(name))
            name = string.Join(" ", new[] { Text(obj, "surname"), Text(obj, "name"), Text(obj, "patronymic") }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(name)) name = Text(obj, "login");
        if (string.IsNullOrWhiteSpace(name)) name = id == 0 ? "Удалённый пользователь" : $"Пользователь #{id}";
        return new(id, name, Text(obj, "post"), Avatar(Text(obj, "avatar_data")));
    }

    private static string? Avatar(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)
            || base64.Length > ((AvatarImage.MaximumReceiveBytes + 2) / 3) * 4) return null;
        try { return AvatarImage.DataUrl(Convert.FromBase64String(base64)); }
        catch (FormatException) { return null; }
    }

    public static ChatListSnapshot ReadChats(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var chats = new Dictionary<ulong, ChatSummary>();
        foreach (var item in Array(root, "chats").EnumerateArray())
        {
            ulong id = Id(item, "id");
            bool group = item.TryGetProperty("is_group", out var flag) && flag.ValueKind == JsonValueKind.True;
            ChatParticipant? participant = !group && item.TryGetProperty("user", out var user)
                && user.ValueKind == JsonValueKind.Object ? Participant(user) : null;
            chats[id] = new(id, group, participant);
        }
        int version = root.TryGetProperty("download_protocol", out var protocol)
            && protocol.ValueKind == JsonValueKind.Number && protocol.TryGetInt32(out int parsed) ? parsed : 0;
        return new(chats.Values.OrderByDescending(c => c.Id).ToArray(), Error(root), version);
    }

    public static HistorySnapshot ReadHistory(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var messages = Array(root, "messages").EnumerateArray().Select(Message);
        ChatParticipant? participant = root.TryGetProperty("user", out var user)
            && user.ValueKind == JsonValueKind.Object ? Participant(user) : null;
        return new(Id(root, "chat_id"), Order(messages), participant, Error(root));
    }

    public static (ulong ChatId, ChatMessage Message) ReadNewMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        return (Id(document.RootElement, "chat_id"), Message(document.RootElement));
    }

    private static ChatMessage Message(JsonElement item)
    {
        var files = new Dictionary<ulong, ChatAttachment>();
        if (item.TryGetProperty("attachments", out var attachments) && attachments.ValueKind != JsonValueKind.Null)
        {
            if (attachments.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid attachments.");
            foreach (var file in attachments.EnumerateArray())
            {
                ulong id = Id(file, "id");
                string name = Text(file, "name");
                long? size = file.TryGetProperty("size", out var value) && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt64(out long length) && length >= 0 ? length : null;
                files[id] = new(id, string.IsNullOrWhiteSpace(name) ? $"Вложение {id}" : name, Text(file, "type"), size);
            }
        }
        long? timestamp = item.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Number
            && time.TryGetInt64(out long seconds) && seconds is >= -62135596800 and <= 253402300799 ? seconds : null;
        if (!item.TryGetProperty("sender", out var sender)) throw new InvalidDataException("Missing sender.");
        return new(Id(item, "id"), Text(item, "content"), timestamp, Participant(sender, true), files.Values.ToArray());
    }

    public static IReadOnlyList<ChatMessage> Order(IEnumerable<ChatMessage> messages) => messages
        .GroupBy(m => m.Id).Select(g => g.Last()).OrderBy(m => m.UnixTime ?? 0).ThenBy(m => m.Id).ToArray();

    private static string? Error(JsonElement root)
    {
        string error = Text(root, "error");
        return string.IsNullOrWhiteSpace(error) ? null : error;
    }
}

public static class ChatPresentation
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    public static string FileSize(long? size) => size switch
    {
        null => "Размер не указан",
        < 1024 => $"{size} Б",
        < 1048576 => $"{(size.Value / 1024d).ToString("0.#", Russian)} КБ",
        _ => $"{(size.Value / 1048576d).ToString("0.#", Russian)} МБ"
    };
    public static DateTimeOffset? LocalTime(long? unixTime) => unixTime is { } value
        ? DateTimeOffset.FromUnixTimeSeconds(value).ToLocalTime() : null;
    public static string Time(long? unixTime) => LocalTime(unixTime)?.ToString("HH:mm", Russian) ?? "—";
    public static string FullTime(long? unixTime) => LocalTime(unixTime)?.ToString("d MMMM yyyy, HH:mm:ss", Russian) ?? "Время не указано";
    public static string Date(long? unixTime)
    {
        if (LocalTime(unixTime) is not { } date) return "Дата не указана";
        return date.Date == DateTime.Today ? "Сегодня" : date.Date == DateTime.Today.AddDays(-1)
            ? "Вчера" : date.ToString("d MMMM yyyy", Russian);
    }
}
