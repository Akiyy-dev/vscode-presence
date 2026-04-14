using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Steamworks;

namespace VSCodePresenceSteamClient;

internal static class Program
{
    private const int TestAppId = 480;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        AppSettings settings;
        try
        {
            settings = LoadSettings();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Failed to load configuration: {exception.Message}");
            return 1;
        }

        if (settings.AppId != TestAppId)
        {
            Console.WriteLine($"Configured AppId {settings.AppId} is ignored for local testing. Forcing AppId {TestAppId}.");
        }

        EnsureSteamAppIdFile(TestAppId);

        using var cancellationTokenSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;

            if (!cancellationTokenSource.IsCancellationRequested)
            {
                Console.WriteLine("Ctrl+C received. Shutting down gracefully...");
                cancellationTokenSource.Cancel();
            }
        };

        var appId = new AppId_t(TestAppId);
        if (SteamAPI.RestartAppIfNecessary(appId))
        {
            Console.WriteLine("Steam requested the process to restart under Steam.");
            return 0;
        }

        Console.WriteLine($"Initializing SteamAPI for AppId {TestAppId}...");
        if (!SteamAPI.Init())
        {
            Console.WriteLine("SteamAPI.Init failed. Make sure Steam is running and the AppId is valid.");
            return 1;
        }

        var richPresenceManager = new RichPresenceManager();
        var webSocketTask = RunWebSocketClientAsync(settings, richPresenceManager, cancellationTokenSource.Token);
        var callbackIntervalMs = Math.Max(16, settings.UpdateIntervalMs);

        try
        {
            while (!cancellationTokenSource.Token.IsCancellationRequested)
            {
                SteamAPI.RunCallbacks();
                await Task.Delay(callbackIntervalMs, cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellationTokenSource.Cancel();

            try
            {
                await webSocketTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Console.WriteLine($"WebSocket task ended with an error: {exception.Message}");
            }

            SteamFriends.ClearRichPresence();
            SteamAPI.Shutdown();
            Console.WriteLine("SteamAPI shutdown completed.");
        }

        return 0;
    }

    private static AppSettings LoadSettings()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        return configuration.Get<AppSettings>() ?? new AppSettings();
    }

    private static void EnsureSteamAppIdFile(int appId)
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, "steam_appid.txt");

        if (File.Exists(filePath))
        {
            Console.WriteLine($"steam_appid.txt already exists at {filePath}.");
            return;
        }

        File.WriteAllText(filePath, appId.ToString());
        Console.WriteLine($"Created steam_appid.txt with AppId {appId}.");
    }

    private static async Task RunWebSocketClientAsync(
        AppSettings settings,
        RichPresenceManager richPresenceManager,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var webSocket = new ClientWebSocket();

            try
            {
                Console.WriteLine($"Connecting to WebSocket server at {settings.WebSocketUrl}...");
                await webSocket.ConnectAsync(new Uri(settings.WebSocketUrl), cancellationToken);
                Console.WriteLine("WebSocket connected.");

                await ReceiveMessagesAsync(webSocket, richPresenceManager, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException exception)
            {
                Console.WriteLine($"WebSocket error: {exception.Message}");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Unexpected WebSocket client error: {exception.Message}");
            }
            finally
            {
                if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Client shutdown",
                            CancellationToken.None);
                    }
                    catch
                    {
                    }
                }

                Console.WriteLine("WebSocket disconnected.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            Console.WriteLine($"Reconnecting in {settings.ReconnectIntervalMs} ms...");

            try
            {
                await Task.Delay(settings.ReconnectIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task ReceiveMessagesAsync(
        ClientWebSocket webSocket,
        RichPresenceManager richPresenceManager,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var messageStream = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open)
        {
            messageStream.SetLength(0);
            WebSocketReceiveResult receiveResult;

            do
            {
                receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("WebSocket server closed the connection.");
                    return;
                }

                if (receiveResult.MessageType == WebSocketMessageType.Text && receiveResult.Count > 0)
                {
                    messageStream.Write(buffer, 0, receiveResult.Count);
                }
            }
            while (!receiveResult.EndOfMessage);

            if (receiveResult.MessageType != WebSocketMessageType.Text)
            {
                Console.WriteLine("Ignoring non-text WebSocket message.");
                continue;
            }

            var message = Encoding.UTF8.GetString(messageStream.GetBuffer(), 0, (int)messageStream.Length);
            HandleWebSocketMessage(message, richPresenceManager);
        }
    }

    private static void HandleWebSocketMessage(string message, RichPresenceManager richPresenceManager)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine("Ignoring message because the root JSON node is not an object.");
                return;
            }

            if (!root.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "status", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Ignoring WebSocket message because type is not status.");
                return;
            }

            if (root.TryGetProperty("version", out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.Number &&
                versionElement.GetInt32() != 1)
            {
                Console.WriteLine("Ignoring status message because version is unsupported.");
                return;
            }

            var payload = root.TryGetProperty("payload", out var payloadElement) && payloadElement.ValueKind == JsonValueKind.Object
                ? ConvertJsonObjectToDictionary(payloadElement)
                : new Dictionary<string, object?>();

            Console.WriteLine($"Received status payload with {payload.Count} field(s).");
            richPresenceManager.UpdatePresence(payload);
            Console.WriteLine($"Rich Presence updated with {payload.Count} fields");
        }
        catch (JsonException exception)
        {
            Console.WriteLine($"Failed to parse WebSocket message: {exception.Message}");
        }
    }

    private static Dictionary<string, object?> ConvertJsonObjectToDictionary(JsonElement jsonObject)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in jsonObject.EnumerateObject())
        {
            result[property.Name] = ConvertJsonValue(property.Value);
        }

        return result;
    }

    private static object? ConvertJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ConvertJsonObjectToDictionary(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonValue).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var int64Value) => int64Value,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

}

internal sealed class AppSettings
{
    public int AppId { get; init; } = 480;

    public string WebSocketUrl { get; init; } = "ws://127.0.0.1:31337";

    public int ReconnectIntervalMs { get; init; } = 3000;

    public int UpdateIntervalMs { get; init; } = 1000;
}