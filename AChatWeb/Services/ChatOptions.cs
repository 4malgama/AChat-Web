namespace AChatWeb.Services;

public sealed record ChatOptions
{
    public string Endpoint { get; init; } = "wss://localhost:8080/ws";
    public int HandshakeTimeoutSeconds { get; init; } = 15;
    public int AuthenticationTimeoutSeconds { get; init; } = 20;
    public int MaximumMessageBytes { get; init; } = 64 * 1024 * 1024;
}

public enum ConnectionPhase
{
    Offline, Connecting, WaitingServerHello, WaitingServerReady,
    Ready, SigningIn, Registering, Authenticated, Error
}

public sealed record ChatUser(ulong Id, string Login, string DisplayName, string? Post = null)
{
    public string Initial => string.IsNullOrWhiteSpace(DisplayName) ? "A" :
        System.Globalization.StringInfo.GetNextTextElement(DisplayName).ToUpperInvariant();
}
