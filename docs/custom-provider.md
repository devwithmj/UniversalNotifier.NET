# Custom providers and channels

## A provider for an existing channel

Derive from `NotifierBase<TMessage>` and implement `SendCoreAsync`. You get validation, the one error model, cancellation, logging and retries for free.

```csharp
public sealed class AcmeSmsNotifier : NotifierBase<SmsMessage>, ISmsNotifier
{
    private readonly AcmeOptions _options;
    private readonly HttpClient _http;

    public AcmeSmsNotifier(AcmeOptions options, HttpClient http, NotifierContext context) : base(context)
    {
        _options = options;
        _http = http;
    }

    protected override string ProviderName => "acme";

    protected override async Task<NotifierResponse> SendCoreAsync(SmsMessage message, CancellationToken cancellationToken)
    {
        // message is already validated. Throw for unexpected failures; they are classified for you.
        using var response = await _http.PostAsJsonAsync("https://api.acme.example/sms", new { to = message.To[0].Value, text = message.Body }, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return NotifierResponse.Accepted(providerMessageId: await response.Content.ReadAsStringAsync(cancellationToken), httpStatusCode: (int)response.StatusCode);
        }

        // Say whether repeating is safe: Transient (not accepted, safe), Permanent, Configuration, Unknown (may have been sent).
        var kind = (int)response.StatusCode == 429 ? NotifierErrorKind.Transient : NotifierErrorKind.Permanent;
        return NotifierResponse.Failed(new NotifierError(kind, "acme.rejected", $"Acme returned HTTP {(int)response.StatusCode}."), (int)response.StatusCode);
    }
}
```

Rules that keep providers consistent:

- Do not put recipient addresses, message text or credentials in error messages or logs.
- For bulk messages return `NotifierResponse.FromRecipients(...)` with one `RecipientResult` per recipient.
- Classify errors honestly. `Unknown` is correct when the request may have reached the provider (timeout, 5xx).
- Override `Classify(Exception)` to recognise your SDK's exception types.

Register it. An unnamed registration becomes the channel's default and is also reachable as the provider name:

```csharp
public static INotifierBuilder AddAcme(this INotifierBuilder builder, Action<AcmeOptions>? configure = null, string? name = null)
{
    var options = builder.Services.AddOptions<AcmeOptions>(name ?? "acme");
    if (configure is not null) options.Configure(configure);
    options.ValidateOnStart();

    return builder.AddNotifier<ISmsNotifier>(
        "acme", name,
        static (services, key) => new AcmeSmsNotifier(
            services.GetRequiredService<IOptionsMonitor<AcmeOptions>>().Get(key),
            services.GetRequiredService<IHttpClientFactory>().CreateClient("acme"),
            services.GetRequiredService<NotifierContext>()));
}
```

Put the extension method in the `Microsoft.Extensions.DependencyInjection` namespace so it is found without extra `using` lines, like the built-in providers. Test it by passing a stub `HttpMessageHandler` to an `HttpClient`.

## A new channel

A channel needs a message type, an interface, a provider, and a handler that connects it to the notification model.

```csharp
public sealed record SlackMessage : NotifierMessage
{
    public string Channel { get; init; } = "";
    public string Text { get; init; } = "";
    public override IReadOnlyList<NotifierError> Validate() => /* return an empty list when valid */ [];
}

public interface ISlackNotifier : INotifier<SlackMessage>;

public sealed class SlackChannel(IServiceProvider services) : INotificationChannel
{
    public NotificationChannel Channel => new("slack");

    public Task<NotifierResponse> SendAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        var notifier = services.GetRequiredService<ISlackNotifier>();   // or a keyed one for delivery.Provider
        return notifier.SendAsync(new SlackMessage { Channel = "#alerts", Text = delivery.Content.Text ?? "" }, cancellationToken);
    }
}

// registration
builder.AddChannel<SlackChannel>();
builder.AddNotifier<ISlackNotifier>("slack", null, (sp, key) => new SlackNotifier(...));

// use
Notification.To(recipient).Via(new NotificationChannel("slack"), new ChannelContent { Text = "Deploy finished: {{Version}}" })
```

`delivery.Content` is already rendered. Templates for the new channel are looked up under the channel name (`templates/welcome/slack.text.txt`).
