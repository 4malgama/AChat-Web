using Microsoft.JSInterop;

namespace AChatWeb.Services;

public sealed class BrowserFileDownloads(IJSRuntime js) : IAsyncDisposable
{
    private Task<IJSObjectReference>? module;

    public async Task SaveAsync(DownloadPayload file)
    {
        var current = await (module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/chat.js").AsTask());
        // Stream interop avoids a second base64 copy of an attachment.
        using var stream = new MemoryStream(file.Bytes, writable: false);
        using var reference = new DotNetStreamReference(stream);
        await current.InvokeVoidAsync("saveFile", file.Name, reference);
    }

    public async ValueTask DisposeAsync()
    {
        if (module is null) return;
        try { await (await module).DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}
