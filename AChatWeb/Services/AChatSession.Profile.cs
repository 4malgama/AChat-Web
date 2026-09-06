using System.Net.WebSockets;
using AChatWeb.Protocol;

namespace AChatWeb.Services;

public sealed partial class AChatSession
{
    private TaskCompletionSource<bool> profileAvailable = NewCompletion();
    private PendingProfile? pendingProfile;
    private PendingAvatar? pendingAvatar;
    public string? AvatarDataUrl { get; private set; }
    public string? ProfileError { get; private set; }
    public string? AvatarError { get; private set; }
    public bool IsUpdatingProfile => pendingProfile is not null;
    public bool IsUpdatingAvatar => pendingAvatar is not null;
    public void ClearProfileError() { ProfileError = null; Notify(); }
    private static TaskCompletionSource<bool> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<bool> WaitForProfileAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated) return false;
        if (User!.ProfileLoaded) return true;
        int epoch = generation;
        try { return await profileAvailable.Task.WaitAsync(TimeSpan.FromSeconds(options.ProfileTimeoutSeconds), cancellationToken); }
        catch (TimeoutException)
        {
            if (epoch == generation)
            {
                ProfileError = "Сервер не прислал личный профиль. Выйдите и войдите снова.";
                Notify();
            }
            return false;
        }
    }

    public async Task<bool> UpdateProfileAsync(ProfileDraft draft, ChatUser baseline)
    {
        if (!IsAuthenticated || User!.Id != baseline.Id || !baseline.ProfileLoaded || IsUpdatingProfile) return false;
        ProfileError = draft.Validate(baseline);
        if (ProfileError is not null) { Notify(); return false; }
        ProfileChangeSet changes = draft.ChangesFrom(baseline);
        if (changes.IsEmpty) return true;
        byte[] bytes;
        try { bytes = PacketWriter.UpdateProfile(changes.ToJson()); }
        catch (ArgumentException)
        {
            ProfileError = "Поля профиля слишком длинные или содержат некорректный символ.";
            Notify();
            return false;
        }
        int epoch = generation;
        var operation = new PendingProfile(changes, NewCompletion());
        pendingProfile = operation;
        Notify();
        try
        {
            await SendAsync(socket!, bytes, lifetime!.Token);
            bool success = await operation.Completion.Task.WaitAsync(TimeSpan.FromSeconds(options.ProfileTimeoutSeconds));
            if (!success && epoch == generation) ProfileError = "Соединение закрыто до подтверждения сохранения.";
            return success;
        }
        catch (TimeoutException)
        {
            if (epoch == generation) ProfileError = "Сервер не подтвердил сохранение. Изменения могли примениться — проверьте профиль после повторного входа.";
            return false;
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidOperationException)
        {
            if (epoch == generation) ProfileError = "Не удалось отправить изменения. Проверьте соединение.";
            return false;
        }
        finally
        {
            if (ReferenceEquals(pendingProfile, operation)) pendingProfile = null;
            if (epoch == generation) Notify();
        }
    }

    public async Task<bool> UpdateAvatarAsync(byte[] image)
    {
        if (!IsAuthenticated || IsUpdatingAvatar) return false;
        AvatarError = null;
        if (image.Length is 0 or > AvatarImage.MaximumUploadBytes || AvatarImage.MimeType(image) != "image/jpeg")
        {
            AvatarError = "Выберите изображение JPEG размером до 1 МБ после обработки.";
            Notify();
            return false;
        }
        int epoch = generation;
        // Keep the expected bytes independent of the caller's buffer until confirmation.
        var operation = new PendingAvatar((byte[])image.Clone(), NewCompletion());
        pendingAvatar = operation;
        Notify();
        try
        {
            await SendAsync(socket!, PacketWriter.UpdateAvatar(operation.Bytes), lifetime!.Token);
            // Java has no upload ACK. Its sequential packet dispatcher processes this
            // request after the upload, so read the stored avatar back before success.
            await SendAsync(socket!, PacketWriter.RequestAvatar(), lifetime.Token);
            bool success = await operation.Completion.Task.WaitAsync(TimeSpan.FromSeconds(options.ProfileTimeoutSeconds));
            if (!success && epoch == generation) AvatarError = "Соединение закрыто до подтверждения новой фотографии.";
            return success;
        }
        catch (TimeoutException)
        {
            if (epoch == generation) AvatarError = "Не удалось подтвердить сохранение фотографии. Проверьте её после повторного входа.";
            return false;
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidOperationException)
        {
            if (epoch == generation) AvatarError = "Не удалось отправить фотографию. Проверьте соединение.";
            return false;
        }
        finally
        {
            if (ReferenceEquals(pendingAvatar, operation)) pendingAvatar = null;
            if (epoch == generation) Notify();
        }
    }

    private void CancelProfileOperations()
    {
        profileAvailable.TrySetResult(false);
        pendingProfile?.Completion.TrySetResult(false);
        pendingAvatar?.Completion.TrySetResult(false);
    }
    private void ResetProfileState()
    {
        CancelProfileOperations();
        profileAvailable = NewCompletion();
        pendingProfile = null;
        pendingAvatar = null;
        ProfileError = AvatarError = AvatarDataUrl = null;
    }
    private sealed record PendingProfile(ProfileChangeSet Changes, TaskCompletionSource<bool> Completion);
    private sealed record PendingAvatar(byte[] Bytes, TaskCompletionSource<bool> Completion);
}
