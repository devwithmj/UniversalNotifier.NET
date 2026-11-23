using System.Text.Json;

namespace UniversalNotifier.NET.Twilio;

/// <summary>
/// Sends WhatsApp messages through the Twilio Messages REST API (the same endpoint and account as SMS, with <c>whatsapp:</c>
/// addresses). Free-form text only reaches a user inside WhatsApp's 24-hour window, or anyone who joined Twilio's sandbox; for
/// everyone else send an approved template (<see cref="WhatsAppMessage.TemplateId"/>, a Twilio Content SID).
/// </summary>
public sealed class TwilioWhatsAppNotifier : NotifierBase<WhatsAppMessage>, IWhatsAppNotifier
{
    /// <summary>The provider name used for explicit selection.</summary>
    public const string Name = "twilio";

    private const string Prefix = "whatsapp:";

    private readonly TwilioApi _api;
    private readonly string _defaultFrom;

    /// <summary>Creates the notifier. With dependency injection use <c>AddTwilio</c> instead.</summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public TwilioWhatsAppNotifier(TwilioOptions options, HttpClient httpClient, NotifierContext context)
        : base(context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        _api = new TwilioApi(options, httpClient);
        _defaultFrom = options.WhatsAppFrom;
    }

    /// <inheritdoc />
    protected override string ProviderName => Name;

    /// <inheritdoc />
    protected override Task<NotifierResponse> SendCoreAsync(WhatsAppMessage message, CancellationToken cancellationToken)
    {
        var sender = message.From ?? _defaultFrom;
        if (string.IsNullOrWhiteSpace(sender))
        {
            return Task.FromResult(TwilioApi.MissingSender("twilio.whatsapp.sender_missing", "WhatsApp"));
        }

        var senderField = TwilioApi.IsMessagingServiceSid(sender)
            ? new KeyValuePair<string, string>("MessagingServiceSid", sender)
            : new KeyValuePair<string, string>("From", WithPrefix(sender));
        var variables = message.TemplateVariables is { Count: > 0 } values
            ? JsonSerializer.Serialize(new Dictionary<string, string>(values), TwilioJsonContext.Default.DictionaryStringString)
            : null;

        return _api.SendEachAsync(
            [.. message.To.Select(t => t.Value)],
            (to, token) =>
            {
                var fields = new List<KeyValuePair<string, string>>(5) { new("To", Prefix + to), senderField };
                if (!string.IsNullOrWhiteSpace(message.TemplateId))
                {
                    fields.Add(new("ContentSid", message.TemplateId));
                    if (variables is not null)
                    {
                        fields.Add(new("ContentVariables", variables));
                    }
                }
                else
                {
                    fields.Add(new("Body", message.Body!));
                }

                return _api.PostAsync(fields, token);
            },
            Classify,
            cancellationToken);
    }

    private static string WithPrefix(string number) =>
        number.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? number : Prefix + number;
}
