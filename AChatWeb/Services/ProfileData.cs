using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace AChatWeb.Services;

public sealed record PrivacyOption(string Key, string Label, IReadOnlyList<PrivacyChoice> Choices);
public sealed record PrivacyChoice(string Value, string Label);

public static class PrivacyCatalog
{
    private static readonly PrivacyChoice[] Visibility = [new("everyone", "Все"), new("friends", "Друзья"), new("onlyme", "Только я")];
    private static readonly PrivacyChoice[] Contact = [new("everyone", "Все"), new("friends", "Друзья"), new("nobody", "Никто")];
    public static readonly IReadOnlyList<PrivacyOption> Options = Array.AsReadOnly<PrivacyOption>([
        new("see_profile_photo", "Кто видит мою фотографию", Visibility),
        new("see_profile_description", "Кто видит описание профиля", Visibility),
        new("see_profile_post", "Кто видит мою должность", Visibility),
        new("see_online_status", "Кто видит мой статус в сети", Visibility),
        new("see_profile_comments", "Кто видит комментарии в профиле", Visibility),
        new("leave_comments", "Кто может оставлять комментарии", Visibility),
        // Qt's "friends of friends" item has no wire value. Don't invent one.
        new("send_friend_request", "Кто может предложить дружбу", [new("everyone", "Все"), new("nobody", "Никто")]),
        new("send_message", "Кто может написать мне", Contact),
        new("invite_to_groups", "Кто может пригласить меня в группу", Contact)
    ]);
}

public sealed class ProfileDraft
{
    public string FirstName { get; set; } = "";
    public string SurName { get; set; } = "";
    public string Patronymic { get; set; } = "";
    public string Post { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, string?> Privacy { get; } = new(StringComparer.Ordinal);
    public string DisplayName
    {
        get => Privacy.GetValueOrDefault("display_name") ?? "";
        set => Privacy["display_name"] = value;
    }
    public string FullName => string.Join(" ", new[] { SurName, FirstName, Patronymic }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public static ProfileDraft FromUser(ChatUser user)
    {
        var draft = new ProfileDraft
        {
            FirstName = user.FirstName, SurName = user.SurName, Patronymic = user.Patronymic,
            Post = user.Post ?? "", Description = user.Description
        };
        foreach (var entry in user.Privacy) draft.Privacy[entry.Key] = entry.Value;
        return draft;
    }

    public ProfileChangeSet ChangesFrom(ChatUser original)
    {
        var fields = new Dictionary<string, string>();
        void Add(string key, string value, string previous) { if (value != previous) fields[key] = value; }
        Add("first_name", FirstName, original.FirstName);
        Add("sur_name", SurName, original.SurName);
        Add("patronymic", Patronymic, original.Patronymic);
        Add("post", Post, original.Post ?? "");
        Add("description", Description, original.Description);
        bool privacyChanged = Privacy.Count != original.Privacy.Count || Privacy.Any(e => !original.Privacy.TryGetValue(e.Key, out var previous) || e.Value != previous);
        return new(fields, privacyChanged ? new Dictionary<string, string?>(Privacy, StringComparer.Ordinal) : null);
    }

    public string? Validate(ChatUser original)
    {
        if (FirstName.Length > 100 || SurName.Length > 100 || Patronymic.Length > 100)
            return "Каждая часть ФИО должна быть не длиннее 100 символов.";
        if (Post.Length > 200) return "Должность должна быть не длиннее 200 символов.";
        // User.Description is an unconstrained @Column String (Hibernate default: 255).
        if (Description.Length > 255) return "Описание должно быть не длиннее 255 символов.";
        if (DisplayName.Length > 100) return "Отображаемое имя должно быть не длиннее 100 символов.";
        foreach (var field in new[] { FirstName, SurName, Patronymic, Post, DisplayName })
            if (field.Any(char.IsControl)) return "В ФИО, должности и отображаемом имени нельзя использовать управляющие символы.";
        // Keep legacy/unknown values unchanged, but never send newly invented values.
        foreach (var option in PrivacyCatalog.Options)
        {
            var value = Privacy.GetValueOrDefault(option.Key);
            if (value != original.Privacy.GetValueOrDefault(option.Key) && !option.Choices.Any(c => c.Value == value))
                return "Выберите допустимое значение настройки приватности.";
        }
        return null;
    }
}

public sealed record ProfileChangeSet(Dictionary<string, string> Fields, Dictionary<string, string?>? Privacy)
{
    public bool IsEmpty => Fields.Count == 0 && Privacy is null;

    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var entry in Fields) writer.WriteString(entry.Key, entry.Value);
            if (Privacy is not null)
            {
                writer.WritePropertyName("privacy");
                writer.WriteStartObject();
                foreach (var entry in Privacy) writer.WriteString(entry.Key, entry.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public bool Matches(ChatUser user)
    {
        string Value(string key) => key switch
        {
            "first_name" => user.FirstName, "sur_name" => user.SurName, "patronymic" => user.Patronymic,
            "post" => user.Post ?? "", "description" => user.Description, _ => throw new InvalidDataException("Unknown profile field.")
        };
        return Fields.All(e => Value(e.Key) == e.Value) &&
            (Privacy is null || Privacy.All(e => user.Privacy.TryGetValue(e.Key, out var value) && value == e.Value));
    }
}

public static class ProfileData
{
    public static ChatUser Read(string json, ChatUser previous)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("profile_info", out var profile) || profile.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Profile object is missing.");
        static string ReadString(JsonElement parent, string key) =>
            parent.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        var privacy = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("privacy_settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
            foreach (var entry in settings.EnumerateObject())
            {
                if (entry.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw new InvalidDataException("Invalid privacy setting.");
                privacy[entry.Name] = entry.Value.GetString();
            }
        // Missing keys are explicit unknown values, never implicit public visibility.
        foreach (var option in PrivacyCatalog.Options) privacy.TryAdd(option.Key, null);
        privacy.TryAdd("display_name", null);
        string login = ReadString(profile, "login");
        if (string.IsNullOrWhiteSpace(login)) login = previous.Login;
        string first = ReadString(profile, "first_name"), last = ReadString(profile, "sur_name"), middle = ReadString(profile, "patronymic");
        string name = string.Join(" ", new[] { last, first, middle }.Where(s => !string.IsNullOrWhiteSpace(s)));
        string display = privacy.GetValueOrDefault("display_name") ?? "";
        return previous with
        {
            Login = login, FirstName = first, SurName = last, Patronymic = middle,
            Post = ReadString(profile, "post"), Description = ReadString(profile, "description"),
            DisplayName = !string.IsNullOrWhiteSpace(display) ? display : name.Length > 0 ? name : login,
            Privacy = new ReadOnlyDictionary<string, string?>(privacy), ProfileLoaded = true
        };
    }
}

public static class AvatarImage
{
    public const int MaximumUploadBytes = 1024 * 1024;
    public const int MaximumReceiveBytes = 8 * 1024 * 1024;
    public static string? MimeType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return "image/jpeg";
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
    public static string? DataUrl(byte[] bytes)
    {
        string? mime = MimeType(bytes);
        return bytes.Length <= MaximumReceiveBytes && mime is not null ? $"data:{mime};base64,{Convert.ToBase64String(bytes)}" : null;
    }
}
