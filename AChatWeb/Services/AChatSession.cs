using System.Net.WebSockets;
using System.Text.Json;
using AChatWeb.Protocol;

namespace AChatWeb.Services;

/// <summary>One connection per browser tab. Credentials are never written to storage or logs.</summary>
public sealed class AChatSession(ChatOptions options) : IAsyncDisposable
{
    private ClientWebSocket? socket;
    private CancellationTokenSource? lifetime;
    private Task? receiveTask;
    private TaskCompletionSource<bool>? ready;
    private TaskCompletionSource<bool>? authentication;
    private readonly SemaphoreSlim connectGate = new(1, 1);
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private int generation;
    private bool submitting;
    private bool disposed;
    private string requestedLogin = "";

    public event Action? Changed;
    public ConnectionPhase Phase { get; private set; } = ConnectionPhase.Offline;
    public ChatUser? User { get; private set; }
    public string? Error { get; private set; }
    public bool IsAuthenticated => Phase == ConnectionPhase.Authenticated && User is not null;
    public bool IsBusy => submitting || Phase is ConnectionPhase.Connecting or
        ConnectionPhase.WaitingServerHello or ConnectionPhase.WaitingServerReady;
    public bool CanAuthenticate => Phase == ConnectionPhase.Ready && !submitting;
    public string ConnectionLabel => Phase switch
    {
        ConnectionPhase.Connecting => "Подключаемся",
        ConnectionPhase.WaitingServerHello or ConnectionPhase.WaitingServerReady => "Проверяем соединение",
        ConnectionPhase.Ready or ConnectionPhase.SigningIn or ConnectionPhase.Registering or ConnectionPhase.Authenticated => "На связи",
        _ => "Нет соединения"
    };
    public bool IsOnline => Phase is ConnectionPhase.Ready or ConnectionPhase.SigningIn or
        ConnectionPhase.Registering or ConnectionPhase.Authenticated;

    public async Task<bool> ConnectAsync()
    {
        await connectGate.WaitAsync();
        try
        {
            if (disposed) return false;
            if (IsOnline) return true;
            await StopAsync();
            Error = null;
            if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) ||
                uri.Scheme != "wss" || !string.IsNullOrEmpty(uri.UserInfo))
            {
                Fault("В настройках приложения указан неверный адрес защищённого соединения.");
                return false;
            }
            socket = new ClientWebSocket();
            lifetime = new CancellationTokenSource();
            ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Phase = ConnectionPhase.Connecting;
            Notify();
            receiveTask = RunAsync(socket, uri, generation, lifetime.Token, ready);
            return await ready.Task;
        }
        finally { connectGate.Release(); }
    }

    public async Task<bool> AuthenticateAsync(bool registration, string login, string password)
    {
        if (submitting || disposed || IsAuthenticated) return false;
        submitting = true;
        Error = null;
        try
        {
            login = login.Trim();
            if (login.Length is < 1 or > 100 || password.Length is < 1 or > 256)
            {
                Error = "Проверьте логин и пароль.";
                return false;
            }
            if (!await ConnectAsync()) return false;
            requestedLogin = login;
            authentication = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Phase = registration ? ConnectionPhase.Registering : ConnectionPhase.SigningIn;
            Notify();
            byte[] packet = PacketWriter.Credentials(registration, login, password);
            try { await SendAsync(socket!, packet, lifetime!.Token); }
            finally { Array.Clear(packet); }

            return await authentication.Task.WaitAsync(
                TimeSpan.FromSeconds(options.AuthenticationTimeoutSeconds));
        }
        catch (System.Text.EncoderFallbackException)
        {
            Reject("Логин или пароль содержит некорректный символ. Введите его ещё раз.");
            return false;
        }
        catch (TimeoutException)
        {
            Fault(registration
                ? "Ответ о регистрации не получен. Аккаунт мог быть создан — попробуйте войти."
                : "Сервер не ответил на запрос входа. Подключитесь ещё раз.");
            lifetime?.Cancel();
            socket?.Abort();
            return false;
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidOperationException)
        {
            Fault("Соединение прервалось. Подключитесь ещё раз и повторите вход.");
            lifetime?.Cancel();
            socket?.Abort();
            return false;
        }
        finally
        {
            submitting = false;
            requestedLogin = "";
            authentication = null;
            Notify();
        }
    }

    public void ClearFormError()
    {
        if (Phase == ConnectionPhase.Ready) { Error = null; Notify(); }
    }

    private async Task RunAsync(ClientWebSocket current, Uri endpoint, int id,
        CancellationToken stop, TaskCompletionSource<bool> handshake)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.HandshakeTimeoutSeconds));
        try
        {
            await current.ConnectAsync(endpoint, deadline.Token);
            if (id != generation) return;
            Phase = ConnectionPhase.WaitingServerHello;
            Notify();
            await SendAsync(current, PacketWriter.Empty(1), deadline.Token);
            byte[] chunk = new byte[16 * 1024];

            while (!stop.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await current.ReceiveAsync(new ArraySegment<byte>(chunk), deadline.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new SessionException("Соединение с сервером закрыто. Войдите ещё раз.");
                    if (result.MessageType != WebSocketMessageType.Binary ||
                        message.Length + result.Count > options.MaximumMessageBytes)
                        throw new InvalidDataException("Invalid WebSocket message.");
                    message.Write(chunk, 0, result.Count);
                } while (!result.EndOfMessage);

                if (id != generation) return;
                await HandlePacketAsync(message.ToArray(), current, deadline.Token);
                if (Phase == ConnectionPhase.Ready && !handshake.Task.IsCompleted)
                {
                    deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                    // The server uses this for localized authentication errors.
                    await SendAsync(current, PacketWriter.Location("RU"), deadline.Token);
                    handshake.TrySetResult(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (id == generation && !stop.IsCancellationRequested)
                Fault("Сервер не ответил вовремя. Проверьте подключение и повторите попытку.");
        }
        catch (SessionException e)
        {
            if (id == generation) Fault(e.Message);
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or System.Text.DecoderFallbackException)
        {
            if (id == generation) Fault("Ответ сервера несовместим с этим клиентом. Нужен протокол AChat 2.0.");
        }
        catch (Exception)
        {
            if (id == generation && !stop.IsCancellationRequested)
                Fault("Не удалось подключиться. Проверьте, что сервер запущен и его сертификату доверяет браузер.");
        }
        finally
        {
            handshake.TrySetResult(false);
            if (id == generation) authentication?.TrySetResult(false);
            current.Abort();
            current.Dispose();
        }
    }

    private async Task HandlePacketAsync(byte[] bytes, ClientWebSocket current, CancellationToken token)
    {
        var reader = new PacketReader(bytes);
        ushort id = reader.UInt16();
        switch (id)
        {
            case 2:
                if (Phase != ConnectionPhase.WaitingServerHello || reader.String16(32) != "2.0")
                    throw new InvalidDataException("Invalid ServerHello.");
                reader.Finish();
                Phase = ConnectionPhase.WaitingServerReady;
                Notify();
                await SendAsync(current, PacketWriter.Empty(3), token);
                return;
            case 4:
                if (Phase != ConnectionPhase.WaitingServerReady)
                    throw new InvalidDataException("Unexpected ServerReady.");
                reader.Finish();
                Phase = ConnectionPhase.Ready;
                Notify();
                return;
            case 101:
                if (Phase != ConnectionPhase.SigningIn) throw new InvalidDataException("Unexpected AuthReject.");
                string reason = reader.String16();
                reader.Finish();
                Reject(string.IsNullOrWhiteSpace(reason) ? "Неверный логин или пароль." : reason);
                return;
            case 102:
                if (Phase != ConnectionPhase.SigningIn) throw new InvalidDataException("Unexpected AuthAccept.");
                ulong uid = reader.UInt64();
                if (reader.Boolean()) reader.String16(); // No token persistence in this release.
                reader.Finish();
                Accept(uid);
                return;
            case 103:
                if (Phase != ConnectionPhase.Registering) throw new InvalidDataException("Unexpected Register.");
                ushort errorCode = reader.UInt16();
                ulong registeredUid = reader.UInt64();
                reader.Finish();
                if (errorCode == 0) Accept(registeredUid);
                else Reject(errorCode switch
                {
                    1 => "Этот логин уже занят. Выберите другой или войдите в свой аккаунт.",
                    2 => "Сервер отклонил пароль. Используйте 8 и более символов, строчную и заглавную латинские буквы и цифру.",
                    3 => "В логине нельзя использовать символ $.",
                    _ => "Не удалось создать аккаунт. Попробуйте ещё раз."
                });
                return;
            case 500:
                if (!IsAuthenticated) throw new InvalidDataException("Profile before authentication.");
                string json = reader.String16();
                reader.Finish();
                using (var document = JsonDocument.Parse(json))
                {
                    if (document.RootElement.TryGetProperty("profile_info", out var profile))
                    {
                        static string Read(JsonElement parent, string key) =>
                            parent.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                                ? value.GetString() ?? "" : "";
                        string login = Read(profile, "login");
                        if (string.IsNullOrWhiteSpace(login)) login = User!.Login;
                        string name = string.Join(" ", new[] { Read(profile, "first_name"), Read(profile, "sur_name") }
                            .Where(s => !string.IsNullOrWhiteSpace(s)));
                        User = User! with { Login = login, DisplayName = name.Length > 0 ? name : login, Post = Read(profile, "post") };
                        Notify();
                    }
                }
                return;
            case 1002:
                if (!IsAuthenticated) throw new InvalidDataException("Chats before authentication.");
                reader.SkipString32();
                reader.Finish();
                return;
            case 200:
                string kickReason = reader.String16();
                reader.Finish();
                throw new SessionException(string.IsNullOrWhiteSpace(kickReason) ? "Сессия завершена сервером." : kickReason);
            default:
                if (!IsAuthenticated) throw new InvalidDataException("Unexpected packet before authentication.");
                // Future chat/profile notification packets do not break an authenticated session.
                return;
        }
    }

    private void Accept(ulong uid)
    {
        if (uid == 0) throw new InvalidDataException("Invalid account id.");
        User = new ChatUser(uid, requestedLogin, requestedLogin);
        Phase = ConnectionPhase.Authenticated;
        Error = null;
        authentication?.TrySetResult(true);
        Notify();
    }

    private void Reject(string reason)
    {
        Phase = ConnectionPhase.Ready;
        Error = reason;
        authentication?.TrySetResult(false);
        Notify();
    }

    private async Task SendAsync(ClientWebSocket target, byte[] bytes, CancellationToken token)
    {
        await sendGate.WaitAsync(token);
        try { await target.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, token); }
        finally { sendGate.Release(); }
    }

    private void Fault(string message)
    {
        Phase = ConnectionPhase.Error;
        User = null;
        Error = message;
        authentication?.TrySetResult(false);
        Notify();
    }

    private async Task StopAsync()
    {
        generation++;
        ready?.TrySetResult(false);
        authentication?.TrySetResult(false);
        lifetime?.Cancel();
        try { socket?.Abort(); } catch (ObjectDisposedException) { }
        if (receiveTask is not null) await receiveTask;
        lifetime?.Dispose();
        lifetime = null;
        socket = null;
        receiveTask = null;
        User = null;
    }

    public async Task LogoutAsync()
    {
        await StopAsync();
        Error = null;
        Phase = ConnectionPhase.Offline;
        Notify();
    }

    private void Notify() => Changed?.Invoke();
    public async ValueTask DisposeAsync() { disposed = true; await StopAsync(); }
    private sealed class SessionException(string message) : Exception(message);
}
