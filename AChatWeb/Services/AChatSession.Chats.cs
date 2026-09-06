using System.Net.WebSockets;
using AChatWeb.Protocol;

namespace AChatWeb.Services;

public sealed record FileDownloadProgress(bool IsLoading, string? Error = null, string? Info = null);

public sealed partial class AChatSession
{
    public const int MaximumDownloadBytes = 32 * 1024 * 1024;
    private TaskCompletionSource<bool> chatsAvailable = NewCompletion();
    private TaskCompletionSource<bool>? listRequest;
    private readonly Dictionary<ulong, ChatHistory> histories = [];
    private readonly Dictionary<ulong, PendingHistory> pendingHistories = [];
    private readonly Dictionary<ulong, PendingDownload> pendingDownloads = [];
    private readonly Dictionary<ulong, FileDownloadProgress> downloadProgress = [];
    private readonly Dictionary<ulong, int> unread = [];
    private ulong nextDownloadRequest;
    private int downloadProtocol;

    public IReadOnlyList<ChatSummary> Chats { get; private set; } = [];
    public bool ChatsLoaded { get; private set; }
    public bool IsRefreshingChats => listRequest is not null;
    public string? ChatsError { get; private set; }
    public ulong? SelectedChatId { get; private set; }
    public bool DownloadSlotsFull => pendingDownloads.Count >= 2;
    public ChatSummary? FindChat(ulong id) => Chats.FirstOrDefault(c => c.Id == id);
    public int UnreadCount(ulong id) => unread.GetValueOrDefault(id);
    public FileDownloadProgress DownloadProgress(ulong fileId) => downloadProgress.GetValueOrDefault(fileId) ?? new(false);

    public ChatHistory History(ulong chatId)
    {
        if (!histories.TryGetValue(chatId, out var history)) histories[chatId] = history = new();
        return history;
    }

    public string ChatPreview(ulong chatId)
    {
        if (!histories.TryGetValue(chatId, out var history) || history.Messages.Count == 0)
            return history?.IsLoaded == true ? "Пока нет сообщений" : "Открыть переписку";
        var last = history.Messages[^1];
        string prefix = last.Sender.Id == User?.Id ? "Вы: " : "";
        return prefix + (!string.IsNullOrWhiteSpace(last.Text) ? last.Text
            : last.Attachments.Count > 0 ? $"Вложение · {last.Attachments[0].Name}" : "Сообщение");
    }

    public void SelectChat(ulong? chatId)
    {
        SelectedChatId = chatId;
        if (chatId is { } id) unread.Remove(id);
        Notify();
    }

    public async Task WaitForChatsAsync()
    {
        if (!IsAuthenticated || ChatsLoaded) return;
        int epoch = generation;
        try { await chatsAvailable.Task.WaitAsync(TimeSpan.FromSeconds(options.HistoryTimeoutSeconds)); }
        catch (TimeoutException)
        {
            if (epoch == generation)
            {
                ChatsError = "Сервер не прислал список чатов. Нажмите «Обновить».";
                Notify();
            }
        }
    }

    public async Task<bool> RefreshChatsAsync()
    {
        if (!IsAuthenticated) return false;
        if (listRequest is { } existing) return await existing.Task;
        int epoch = generation;
        var operation = NewCompletion();
        listRequest = operation;
        ChatsError = null;
        Notify();
        try
        {
            await SendAsync(socket!, PacketWriter.RequestChats(), lifetime!.Token);
            return await operation.Task.WaitAsync(TimeSpan.FromSeconds(options.HistoryTimeoutSeconds));
        }
        catch (TimeoutException)
        {
            if (epoch == generation) ChatsError = "Не удалось обновить чаты. Проверьте соединение и версию сервера.";
            return false;
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidOperationException)
        {
            if (epoch == generation) ChatsError = "Не удалось запросить список чатов.";
            return false;
        }
        finally
        {
            operation.TrySetResult(false);
            if (ReferenceEquals(listRequest, operation)) listRequest = null;
            if (epoch == generation) Notify();
        }
    }

    public async Task LoadMessagesAsync(ulong chatId, bool reload = false)
    {
        if (!IsAuthenticated || FindChat(chatId) is null) return;
        var history = History(chatId);
        if (pendingHistories.TryGetValue(chatId, out var existing))
        {
            await existing.Completion.Task;
            return;
        }
        if (history.IsLoaded && !reload) return;
        int epoch = generation;
        var operation = new PendingHistory(NewCompletion());
        pendingHistories[chatId] = operation;
        history.IsLoading = true;
        history.Error = null;
        Notify();
        try
        {
            await SendAsync(socket!, PacketWriter.RequestMessages(chatId), lifetime!.Token);
            await operation.Completion.Task.WaitAsync(TimeSpan.FromSeconds(options.HistoryTimeoutSeconds));
        }
        catch (TimeoutException)
        {
            if (epoch == generation) history.Error = "Сервер не ответил на запрос истории. Попробуйте ещё раз.";
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidOperationException)
        {
            if (epoch == generation) history.Error = "Не удалось загрузить сообщения. Проверьте соединение.";
        }
        finally
        {
            operation.Completion.TrySetResult(false);
            if (pendingHistories.GetValueOrDefault(chatId) == operation) pendingHistories.Remove(chatId);
            if (epoch == generation) { history.IsLoading = false; Notify(); }
        }
    }

    private void ApplyChats(ChatListSnapshot snapshot)
    {
        ChatsError = snapshot.Error;
        if (snapshot.Error is null)
        {
            // Replace the full snapshot. Hidden/missing avatars and posts must not survive a refresh.
            Chats = snapshot.Chats;
            downloadProtocol = snapshot.DownloadProtocol;
            var ids = Chats.Select(c => c.Id).ToHashSet();
            foreach (ulong id in histories.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                histories.Remove(id);
                unread.Remove(id);
                if (pendingHistories.Remove(id, out var pending)) pending.Completion.TrySetResult(false);
            }
            foreach (var pending in pendingDownloads.Values.Where(p => !ids.Contains(p.ChatId)))
                pending.Completion.TrySetResult(null);
            ChatsLoaded = true;
        }
        chatsAvailable.TrySetResult(snapshot.Error is null);
        listRequest?.TrySetResult(snapshot.Error is null);
        Notify();
    }

    private void ApplyHistory(HistorySnapshot snapshot)
    {
        // A response for A must never replace the visible history of B.
        if (FindChat(snapshot.ChatId) is null) return;
        var history = History(snapshot.ChatId);
        pendingHistories.TryGetValue(snapshot.ChatId, out var operation);
        history.Error = snapshot.Error;
        history.Messages = snapshot.Error is null
            ? ChatData.Order(snapshot.Messages.Concat(operation?.Live.Values.AsEnumerable() ?? [])) : [];
        history.IsLoaded = snapshot.Error is null;
        history.Revision++;
        if (snapshot.Participant is { } participant)
            Chats = Chats.Select(c => c.Id == snapshot.ChatId ? c with { Participant = participant } : c).ToArray();
        if (SelectedChatId == snapshot.ChatId) unread.Remove(snapshot.ChatId);
        operation?.Completion.TrySetResult(snapshot.Error is null);
        Notify();
    }

    private void ApplyNewMessage(ulong chatId, ChatMessage message)
    {
        if (FindChat(chatId) is null)
        {
            _ = RefreshChatsAsync();
            return;
        }
        var history = History(chatId);
        bool duplicate = history.Messages.Any(m => m.Id == message.Id);
        history.Messages = ChatData.Order(history.Messages.Append(message));
        history.Revision++;
        if (pendingHistories.TryGetValue(chatId, out var operation)) operation.Live[message.Id] = message;
        if (!duplicate && SelectedChatId != chatId && message.Sender.Id != User?.Id)
            unread[chatId] = Math.Min(999, unread.GetValueOrDefault(chatId) + 1);
        Notify();
    }

    public async Task<DownloadPayload?> DownloadAttachmentAsync(ulong chatId, ulong fileId)
    {
        if (!IsAuthenticated || DownloadProgress(fileId).IsLoading || DownloadSlotsFull) return null;
        if (FindChat(chatId) is null || !histories.TryGetValue(chatId, out var history)
            || !history.Messages.Any(m => m.Attachments.Any(a => a.Id == fileId))) return null;
        if (downloadProtocol < 2)
        {
            SetDownloadError(fileId, "Для скачивания установите серверную часть этого обновления.");
            return null;
        }
        var attachment = history.Messages.SelectMany(m => m.Attachments).First(a => a.Id == fileId);
        if (attachment.Size > MaximumDownloadBytes)
        {
            SetDownloadError(fileId, "Файл больше 32 МБ — текущего лимита скачивания.");
            return null;
        }
        int epoch = generation;
        ulong requestId = ++nextDownloadRequest;
        var operation = new PendingDownload(chatId, fileId, new(TaskCreationOptions.RunContinuationsAsynchronously));
        pendingDownloads[requestId] = operation;
        downloadProgress[fileId] = new(true);
        Notify();
        try
        {
            await SendAsync(socket!, PacketWriter.DownloadFile(fileId, requestId), lifetime!.Token);
            var result = await operation.Completion.Task.WaitAsync(TimeSpan.FromSeconds(options.DownloadTimeoutSeconds));
            if (epoch != generation || !IsAuthenticated) return null;
            if (result is null && DownloadProgress(fileId).Error is null)
                downloadProgress[fileId] = new(false, "Скачивание отменено: чат или соединение недоступны.");
            return result;
        }
        catch (TimeoutException)
        {
            if (epoch == generation) downloadProgress[fileId] = new(false, "Сервер не ответил. Нажмите, чтобы повторить скачивание.");
            return null;
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidOperationException)
        {
            if (epoch == generation) downloadProgress[fileId] = new(false, "Не удалось скачать файл. Проверьте соединение.");
            return null;
        }
        finally
        {
            pendingDownloads.Remove(requestId);
            if (epoch == generation)
            {
                var progress = DownloadProgress(fileId);
                downloadProgress[fileId] = progress with { IsLoading = false };
                Notify();
            }
        }
    }

    private void ReceiveDownload(PacketReader reader)
    {
        ulong requestId = reader.UInt64(), fileId = reader.UInt64();
        ushort status = reader.UInt16();
        string error = reader.String16(4096);
        string name = reader.String32(1024);
        byte[] bytes = reader.Bytes32(MaximumDownloadBytes);
        reader.Finish();
        if (!pendingDownloads.TryGetValue(requestId, out var pending)) return; // Late response after timeout.
        if (pending.FileId != fileId) throw new InvalidDataException("Mismatched download response.");
        if (status != 0)
        {
            downloadProgress[fileId] = new(false, string.IsNullOrWhiteSpace(error) ? "Вложение недоступно." : error);
            pending.Completion.TrySetResult(null);
        }
        else pending.Completion.TrySetResult(new(fileId, name, bytes)); // Zero-byte files are valid.
    }

    public void SetDownloadError(ulong fileId, string error)
    {
        if (!IsAuthenticated) return;
        downloadProgress[fileId] = new(false, error);
        Notify();
    }

    public void MarkDownloadHandedToBrowser(ulong fileId)
    {
        if (!IsAuthenticated) return;
        downloadProgress[fileId] = new(false, Info: "Передан браузеру · скачать ещё раз");
        Notify();
    }

    private void ResetChatState()
    {
        chatsAvailable.TrySetResult(false);
        listRequest?.TrySetResult(false);
        foreach (var operation in pendingHistories.Values) operation.Completion.TrySetResult(false);
        foreach (var operation in pendingDownloads.Values) operation.Completion.TrySetResult(null);
        chatsAvailable = NewCompletion();
        listRequest = null;
        pendingHistories.Clear();
        pendingDownloads.Clear();
        histories.Clear();
        downloadProgress.Clear();
        unread.Clear();
        Chats = [];
        ChatsLoaded = false;
        ChatsError = null;
        SelectedChatId = null;
        downloadProtocol = 0;
    }

    private sealed record PendingHistory(TaskCompletionSource<bool> Completion)
    {
        public Dictionary<ulong, ChatMessage> Live { get; } = [];
    }
    private sealed record PendingDownload(ulong ChatId, ulong FileId, TaskCompletionSource<DownloadPayload?> Completion);
}
