namespace UniversalNotifier.NET.Twilio;

/// <summary>
/// Sends SMS through the Twilio Messages REST API. Twilio sends one message per recipient, so a message with several
/// recipients makes one call each (a few in parallel) and reports every recipient separately.
/// </summary>
public sealed class TwilioSmsNotifier : NotifierBase<SmsMessage>, ISmsNotifier
{
    /// <summary>The provider name used for explicit selection.</summary>
    public const string Name = "twilio";

    private readonly TwilioApi _api;
    private readonly string _defaultFrom;

    /// <summary>Creates the notifier. With dependency injection use <c>AddTwilio</c> instead.</summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public TwilioSmsNotifier(TwilioOptions options, HttpClient httpClient, NotifierContext context)
        : base(context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        _api = new TwilioApi(options, httpClient);
        _defaultFrom = options.From;
    }

    /// <inheritdoc />
    protected override string ProviderName => Name;

    /// <inheritdoc />
    protected override Task<NotifierResponse> SendCoreAsync(SmsMessage message, CancellationToken cancellationToken)
    {
        var sender = message.From ?? _defaultFrom;
        if (string.IsNullOrWhiteSpace(sender))
        {
            return Task.FromResult(TwilioApi.MissingSender("twilio.sms.sender_missing", "SMS"));
        }

        return _api.SendEachAsync(
            [.. message.To.Select(t => t.Value)],
            (to, token) => _api.PostAsync(
                [
                    new("To", to),
                    new(TwilioApi.IsMessagingServiceSid(sender) ? "MessagingServiceSid" : "From", sender),
                    new("Body", message.Body),
                ],
                token),
            Classify,
            cancellationToken);
    }
}
