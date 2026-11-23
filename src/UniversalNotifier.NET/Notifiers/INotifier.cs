namespace UniversalNotifier.NET;

/// <summary>Sends one kind of message through one provider.</summary>
/// <typeparam name="TMessage">The message type this notifier sends.</typeparam>
public interface INotifier<in TMessage> where TMessage : NotifierMessage
{
    /// <summary>
    /// Validates and sends a message. Delivery problems are returned in the <see cref="NotifierResponse"/>, not thrown.
    /// Only programmer errors (a <see langword="null"/> message) and cancellation throw.
    /// </summary>
    Task<NotifierResponse> SendAsync(TMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Sends email. Implemented by SMTP, Mailgun and other email providers.</summary>
public interface IEmailNotifier : INotifier<EmailMessage>;

/// <summary>Sends text messages. Implemented by Twilio and other SMS gateways.</summary>
public interface ISmsNotifier : INotifier<SmsMessage>;

/// <summary>Sends mobile push notifications. Implemented by Firebase Cloud Messaging.</summary>
public interface IPushNotifier : INotifier<PushMessage>;

/// <summary>Sends Telegram bot messages.</summary>
public interface ITelegramNotifier : INotifier<TelegramMessage>;

/// <summary>Sends browser (Web Push) notifications.</summary>
public interface IWebPushNotifier : INotifier<WebPushMessage>;
