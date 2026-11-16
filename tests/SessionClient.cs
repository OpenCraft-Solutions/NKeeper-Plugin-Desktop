using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NKeeper.Testing;

namespace NKeeper.Plugins.Desktop.Tests;

/// <summary>The browser's end of a session, against a real test server.</summary>
internal sealed class SessionClient(ClientWebSocket socket) : IAsyncDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static async Task<SessionClient> ConnectAsync(TestServer server, string cookie, string type)
    {
        var socket = new ClientWebSocket();
        try
        {
            // Browsers don't send antiforgery tokens with WebSockets, so core checks the origin instead (architecture.md §7.10).
            socket.Options.SetRequestHeader("Origin", server.BaseAddress.GetLeftPart(UriPartial.Authority));
            socket.Options.SetRequestHeader("Cookie", cookie);
            await socket.ConnectAsync(new Uri($"ws://{server.BaseAddress.Authority}/api/sessions/{type}"), Token);
            return new SessionClient(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public Task SendAsync(object message) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, JsonSerializerOptions.Web), WebSocketMessageType.Text, true, Token);

    /// <summary>A raw text frame: Guacamole instructions, say.</summary>
    public Task SendTextAsync(string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, Token);

    /// <summary>Reads text frames until <paramref name="text"/> appears in what came, and returns all of it.</summary>
    public async Task<string> ReadTextUntilAsync(string text)
    {
        var received = new StringBuilder();
        while (!received.ToString().Contains(text, StringComparison.Ordinal))
        {
            var (kind, data) = await ReceiveAsync();
            Assert.True(kind != WebSocketMessageType.Close, $"The session closed before {text}. It sent: {received}");
            if (kind == WebSocketMessageType.Text)
            {
                received.Append(Encoding.UTF8.GetString(data));
            }
        }

        return received.ToString();
    }

    /// <summary>Reads until a text message of <paramref name="type"/>, failing on a different control message.</summary>
    public async Task<JsonElement> ExpectAsync(string type)
    {
        while (true)
        {
            var (kind, data) = await ReceiveAsync();
            if (kind == WebSocketMessageType.Close)
            {
                Assert.Fail($"The session closed while waiting for {type}.");
            }

            if (kind != WebSocketMessageType.Text)
            {
                continue;
            }

            var message = JsonDocument.Parse(data).RootElement;
            var actual = message.GetProperty("type").GetString();
            if (actual == type)
            {
                return message;
            }

            Assert.False(actual is "error" or "closed", $"Expected {type}, got {Encoding.UTF8.GetString(data)}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
        }

        socket.Dispose();
    }

    private async Task<(WebSocketMessageType Kind, byte[] Data)> ReceiveAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (WebSocketMessageType.Close, []);
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return (result.MessageType, message.ToArray());
            }
        }
    }
}
