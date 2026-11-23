using Microsoft.Extensions.DependencyInjection;

namespace UniversalNotifier.NET;

// Each handler turns the rendered content plus the recipient's address for that channel into a typed message
// and hands it to the registered provider. Providers are resolved per call so named providers can be chosen.
internal static class ChannelSupport
{
    public static TNotifier? Resolve<TNotifier>(IServiceProvider services, string? provider) where TNotifier : class =>
        provider is null ? services.GetService<TNotifier>() : services.GetKeyedService<TNotifier>(provider);

    public static NotifierResponse NoProvider(NotificationChannel channel, string? provider) =>
        NotifierResponse.Failed(new NotifierError(
            NotifierErrorKind.Configuration,
            $"{channel}.provider.missing",
            provider is null
                ? $"No {channel} provider is registered. Register one, for example with the provider's Add method on the notifier builder."
                : $"No {channel} provider named '{provider}' is registered."));

    public static NotifierResponse NoAddress(NotificationChannel channel, string what) =>
        NotifierResponse.Failed(new NotifierError(NotifierErrorKind.Validation, $"{channel}.address.missing", $"The recipient has no {what}."));
}

internal sealed class EmailChannel(IServiceProvider services) : INotificationChannel
{
    public NotificationChannel Channel => NotificationChannel.Email;

    public Task<NotifierResponse> SendAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.Notification.Recipient.Email is not { } email)
        {
            return Task.FromResult(ChannelSupport.NoAddress(Channel, "email address"));
        }

        var notifier = ChannelSupport.Resolve<IEmailNotifier>(services, delivery.Provider);
        if (notifier is null)
        {
            return Task.FromResult(ChannelSupport.NoProvider(Channel, delivery.Provider));
        }

        return notifier.SendAsync(
            new EmailMessage
            {
                Id = delivery.Notification.Id,
                Metadata = delivery.Notification.Metadata,
                To = [email],
                Subject = delivery.Content.Subject ?? string.Empty,
                TextBody = delivery.Content.Text,
                HtmlBody = delivery.Content.Html,
            },
            cancellationToken);
    }
}

internal sealed class SmsChannel(IServiceProvider services) : INotificationChannel
{
    public NotificationChannel Channel => NotificationChannel.Sms;

    public Task<NotifierResponse> SendAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.Notification.Recipient.Phone is not { } phone)
        {
            return Task.FromResult(ChannelSupport.NoAddress(Channel, "phone number"));
        }

        var notifier = ChannelSupport.Resolve<ISmsNotifier>(services, delivery.Provider);
        if (notifier is null)
        {
            return Task.FromResult(ChannelSupport.NoProvider(Channel, delivery.Provider));
        }

        return notifier.SendAsync(
            new SmsMessage
            {
                Id = delivery.Notification.Id,
                Metadata = delivery.Notification.Metadata,
                To = [phone],
                Body = delivery.Content.Text ?? string.Empty,
            },
            cancellationToken);
    }
}

internal sealed class PushChannel(IServiceProvider services) : INotificationChannel
{
    public NotificationChannel Channel => NotificationChannel.Push;

    public Task<NotifierResponse> SendAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        var tokens = delivery.Notification.Recipient.DeviceTokens;
        if (tokens.Count == 0)
        {
            return Task.FromResult(ChannelSupport.NoAddress(Channel, "device token"));
        }

        var notifier = ChannelSupport.Resolve<IPushNotifier>(services, delivery.Provider);
        if (notifier is null)
        {
            return Task.FromResult(ChannelSupport.NoProvider(Channel, delivery.Provider));
        }

        return notifier.SendAsync(
            new PushMessage
            {
                Id = delivery.Notification.Id,
                Metadata = delivery.Notification.Metadata,
                Tokens = tokens,
                Title = delivery.Content.Title,
                Body = delivery.Content.Text,
                Data = delivery.Content.Data,
            },
            cancellationToken);
    }
}

internal sealed class WebPushChannel(IServiceProvider services) : INotificationChannel
{
    public NotificationChannel Channel => NotificationChannel.WebPush;

    public Task<NotifierResponse> SendAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        var subscriptions = delivery.Notification.Recipient.WebPushSubscriptions;
        if (subscriptions.Count == 0)
        {
            return Task.FromResult(ChannelSupport.NoAddress(Channel, "web push subscription"));
        }

        var notifier = ChannelSupport.Resolve<IWebPushNotifier>(services, delivery.Provider);
        if (notifier is null)
        {
            return Task.FromResult(ChannelSupport.NoProvider(Channel, delivery.Provider));
        }

        return notifier.SendAsync(
            new WebPushMessage
            {
                Id = delivery.Notification.Id,
                Metadata = delivery.Notification.Metadata,
                Subscriptions = subscriptions,
                Title = delivery.Content.Title,
                Body = delivery.Content.Text,
                Data = delivery.Content.Data,
            },
            cancellationToken);
    }
}

internal sealed class TelegramChannel(IServiceProvider services) : INotificationChannel
{
    public NotificationChannel Channel => NotificationChannel.Telegram;

    public Task<NotifierResponse> SendAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.Notification.Recipient.TelegramChatId is not { } chatId)
        {
            return Task.FromResult(ChannelSupport.NoAddress(Channel, "Telegram chat id"));
        }

        var notifier = ChannelSupport.Resolve<ITelegramNotifier>(services, delivery.Provider);
        if (notifier is null)
        {
            return Task.FromResult(ChannelSupport.NoProvider(Channel, delivery.Provider));
        }

        var html = delivery.Content.Html;
        return notifier.SendAsync(
            new TelegramMessage
            {
                Id = delivery.Notification.Id,
                Metadata = delivery.Notification.Metadata,
                ChatIds = [chatId],
                Text = html ?? delivery.Content.Text ?? string.Empty,
                ParseMode = html is null ? TelegramParseMode.None : TelegramParseMode.Html,
            },
            cancellationToken);
    }
}
