using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UniversalNotifier.NET.Internal;

namespace UniversalNotifier.NET.Telegram;

/// <summary>
/// Sends Telegram bot messages through the Bot API. Each chat id gets its own request (a few in parallel) and its own
/// result. The bot token is part of every request URL, so this notifier uses its own <see cref="HttpClient"/> instead of
/// <c>IHttpClientFactory</c>, whose default logging would write the URL, and the token with it, to your logs.
/// </summary>
public sealed class TelegramNotifier : NotifierBase<TelegramMessage>, ITelegramNotifier, IDisposable
{
    /// <summary>The provider name used for explicit selection.</summary>
    public const string Name = "telegram";

    private const int MaxTextLength = 4096;

    private readonly HttpClient _http;
    private readonly Uri _sendMessageUri;
    private readonly int _maxConcurrency;

    /// <summary>Creates the notifier. With dependency injection use <c>AddTelegram</c> instead.</summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public TelegramNotifier(TelegramOptions options, NotifierContext context)
        : this(
            options,
            context,
            new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) // picks up DNS changes on long-running services
    {
    }

    internal TelegramNotifier(TelegramOptions options, NotifierContext context, HttpMessageHandler handler)
        : base(context)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (TelegramOptions.Validate(options) is { } error)
        {
            handler.Dispose();
            throw new ArgumentException(error, nameof(options));
        }

        _http = new HttpClient(handler, disposeHandler: true) { Timeout = options.Timeout };
        _maxConcurrency = options.MaxConcurrency;
        _sendMessageUri = new Uri(options.BaseAddress, $"/bot{options.BotToken}{(options.UseTestEnvironment ? "/test" : string.Empty)}/sendMessage");
    }

    /// <inheritdoc />
    protected override string ProviderName => Name;

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    /// <inheritdoc />
    protected override async Task<NotifierResponse> SendCoreAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        if (message.Text.Length > MaxTextLength)
        {
            return NotifierResponse.Failed(new NotifierError(NotifierErrorKind.Validation, "telegram.text.too_long", $"Telegram messages are limited to {MaxTextLength} characters."));
        }

        if (message.ChatIds.Count == 1)
        {
            return await SendToAsync(message, message.ChatIds[0], cancellationToken).ConfigureAwait(false);
        }

        var results = new RecipientResult[message.ChatIds.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, results.Length),
            new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrency, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var chatId = message.ChatIds[index];
                try
                {
                    var response = await SendToAsync(message, chatId, token).ConfigureAwait(false);
                    results[index] = new RecipientResult(chatId, response.Success, response.ProviderMessageId, response.Success ? null : response.Errors[0]);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    results[index] = new RecipientResult(chatId, false, Error: Classify(exception));
                }
            }).ConfigureAwait(false);

        return NotifierResponse.FromRecipients(results);
    }

    private async Task<NotifierResponse> SendToAsync(TelegramMessage message, string chatId, CancellationToken cancellationToken)
    {
        var body = new SendMessageRequest(
            chatId,
            message.Text,
            message.ParseMode switch
            {
                TelegramParseMode.Html => "HTML",
                TelegramParseMode.MarkdownV2 => "MarkdownV2",
                _ => null,
            },
            message.Silent ? true : null);

        using var request = new HttpRequestMessage(HttpMethod.Post, _sendMessageUri)
        {
            Content = JsonContent.Create(body, TelegramJsonContext.Default.SendMessageRequest),
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        using var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            return NotifierResponse.Accepted(ReadMessageId(json), status);
        }

        var description = ProviderHttp.GetString(json, "description");
        var retryAfter = ReadRetryAfter(json) ?? ProviderHttp.RetryAfter(response);
        var error = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.NotFound =>
                new NotifierError(NotifierErrorKind.Configuration, "telegram.unauthorized", "Telegram rejected the bot token."),
            HttpStatusCode.Forbidden =>
                new NotifierError(NotifierErrorKind.Permanent, "telegram.blocked", $"Telegram refused delivery to this chat{Suffix(description)}"),
            _ => ProviderHttp.ClassifyStatus(response.StatusCode, "Telegram", description),
        };

        return NotifierResponse.Failed(error, status, retryAfter);
    }

    private static string Suffix(string? description) => string.IsNullOrWhiteSpace(description) ? "." : $": {description}";

    private static string? ReadMessageId(JsonDocument? json) =>
        json is { RootElement: { ValueKind: JsonValueKind.Object } root }
        && root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty("message_id", out var id) && id.ValueKind == JsonValueKind.Number
            ? id.GetRawText()
            : null;

    private static TimeSpan? ReadRetryAfter(JsonDocument? json) =>
        json is { RootElement: { ValueKind: JsonValueKind.Object } root }
        && root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty("retry_after", out var seconds) && seconds.TryGetInt32(out var value)
            ? TimeSpan.FromSeconds(value)
            : null;
}
