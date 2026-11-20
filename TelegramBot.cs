using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

// Talks to the Bot API over long polling. The token and the public address come from
// configuration, so a missing token simply turns the bot off and leaves the web game working.
internal sealed class TelegramBot(IConfiguration config, ILogger<TelegramBot> log) : BackgroundService {
    readonly string token = config["Telegram:BotToken"]?.Trim() ?? "";
    readonly string publicUrl = (config["Telegram:PublicUrl"]?.Trim() ?? "").TrimEnd('/');
    readonly string configuredLink = (config["Telegram:AppLink"]?.Trim() ?? "").TrimEnd('/');
    // Some networks reach only part of the Telegram address pool, so the address may be pinned.
    // The host name stays api.telegram.org, which keeps the certificate check honest.
    readonly IPAddress? apiAddress =
        IPAddress.TryParse(config["Telegram:ApiAddress"]?.Trim(), out var pinned) ? pinned : null;
    string? username;
    string? appLink;

    public object? PublicSettings => appLink is null ? null
        : new { inviteBase = appLink, bot = username is null ? null : "@" + username };

    // A pinned address is tried first and the ordinary name lookup is the fallback, so a moved
    // Telegram endpoint costs one slow attempt instead of a dead bot. Some networks drop a share
    // of new connections, so the pinned address gets a few tries before the name is trusted again.
    HttpClient Client() {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(25) };
        if (apiAddress is not null)
            handler.ConnectCallback = async (context, stopping) => {
                for (var attempt = 0; attempt < 3; attempt++) {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    deadline.CancelAfter(TimeSpan.FromSeconds(8));
                    try {
                        return await Connect(apiAddress, context.DnsEndPoint.Port, deadline.Token);
                    } catch (Exception error) when (!stopping.IsCancellationRequested) {
                        log.LogDebug(error, "Pinned Telegram address {Address} missed on attempt {Attempt}",
                            apiAddress, attempt + 1);
                    }
                }
                return await Connect(context.DnsEndPoint, stopping);
            };
        if (apiAddress is not null) log.LogInformation("Telegram API address is pinned to {Address}", apiAddress);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(70) };
    }

    static async ValueTask<Stream> Connect(IPAddress address, int port, CancellationToken stopping) {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try {
            await socket.ConnectAsync(address, port, stopping);
            return new NetworkStream(socket, ownsSocket: true);
        } catch {
            socket.Dispose();
            throw;
        }
    }

    static async ValueTask<Stream> Connect(DnsEndPoint endpoint, CancellationToken stopping) {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try {
            await socket.ConnectAsync(endpoint, stopping);
            return new NetworkStream(socket, ownsSocket: true);
        } catch {
            socket.Dispose();
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stopping) {
        if (token.Length == 0) {
            log.LogInformation("Telegram bot is off: Telegram:BotToken is empty");
            return;
        }
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps) {
            log.LogWarning("Telegram bot is off: Telegram:PublicUrl must be an absolute https address");
            return;
        }

        using var http = Client();
        while (!stopping.IsCancellationRequested) {
            try {
                using (var me = await Call(http, "getMe", new { }, stopping))
                    username = me.RootElement.GetProperty("result").GetProperty("username").GetString();
                if (username is null) throw new TelegramError("getMe", "the bot has no username", 0);
                appLink = configuredLink.Length > 0 ? configuredLink : $"https://t.me/{username}";

                using (var hook = await Call(http, "getWebhookInfo", new { }, stopping))
                    if (hook.RootElement.GetProperty("result").TryGetProperty("url", out var url) &&
                        url.GetString() is { Length: > 0 } old)
                        log.LogWarning("Telegram bot had a webhook ({Webhook}); polling replaces it", old);
                await Call(http, "deleteWebhook", new { drop_pending_updates = false }, stopping);
                await Call(http, "setChatMenuButton", new {
                    menu_button = new { type = "web_app", text = "Играть", web_app = new { url = publicUrl + "/" } }
                }, stopping);
                log.LogInformation("Telegram bot @{Bot} is on: menu button opens {Url}, invite base {Link}",
                    username, publicUrl, appLink);
                break;
            } catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
                return;
            } catch (Exception error) {
                log.LogWarning(error, "Telegram bot is not ready, retrying");
                try { await Task.Delay(TimeSpan.FromSeconds(15), stopping); }
                catch (OperationCanceledException) { return; }
            }
        }

        long offset = 0;
        while (!stopping.IsCancellationRequested) {
            try {
                using var updates = await Call(http, "getUpdates", new {
                    offset, timeout = 30, allowed_updates = new[] { "message" }
                }, stopping);
                foreach (var update in updates.RootElement.GetProperty("result").EnumerateArray()) {
                    offset = Math.Max(offset, update.GetProperty("update_id").GetInt64() + 1);
                    if (update.TryGetProperty("message", out var message)) await Greet(http, message, stopping);
                }
            } catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
                break;
            } catch (TelegramError error) {
                log.LogWarning("Telegram {Method} refused: {Description}", error.Method, error.Description);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(error.RetryAfter, 5)), stopping); }
                catch (OperationCanceledException) { break; }
            } catch (Exception error) {
                log.LogWarning(error, "Telegram polling failed, retrying");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stopping); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    async Task Greet(HttpClient http, JsonElement message, CancellationToken stopping) {
        if (!message.TryGetProperty("text", out var text) ||
            !(text.GetString() ?? "").StartsWith("/start", StringComparison.Ordinal)) return;
        if (!message.TryGetProperty("chat", out var chat) ||
            chat.GetProperty("type").GetString() != "private") return;
        await Call(http, "sendMessage", new {
            chat_id = chat.GetProperty("id").GetInt64(),
            parse_mode = "HTML",
            text = "🏰 <b>Truck-kun Tower</b>\n\n" +
                "Дуэль на двоих: один держит крепость в конце лабиринта, второй покупает слизней и выбирает входы.\n\n" +
                "Открой игру, создай дуэль и пришли ссылку-приглашение сопернику.",
            reply_markup = new {
                inline_keyboard = new[] {
                    new[] { new { text = "Открыть игру", web_app = new { url = publicUrl + "/" } } }
                }
            }
        }, stopping);
    }

    async Task<JsonDocument> Call(HttpClient http, string method, object payload, CancellationToken stopping) {
        using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/{method}", payload, stopping);
        var body = await response.Content.ReadAsStreamAsync(stopping);
        var document = await JsonDocument.ParseAsync(body, cancellationToken: stopping);
        if (document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return document;

        var description = document.RootElement.TryGetProperty("description", out var text)
            ? text.GetString() ?? "unknown error" : "unknown error";
        var retry = 0;
        if (document.RootElement.TryGetProperty("parameters", out var parameters) &&
            parameters.TryGetProperty("retry_after", out var after)) retry = after.GetInt32();
        document.Dispose();
        throw new TelegramError(method, description, retry);
    }

    sealed class TelegramError(string method, string description, int retryAfter)
        : Exception($"{method}: {description}") {
        public string Method { get; } = method;
        public string Description { get; } = description;
        public int RetryAfter { get; } = retryAfter;
    }
}
