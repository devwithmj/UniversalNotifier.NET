using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace UniversalNotifier.NET.Testing;

/// <summary>
/// A notifier that sends nothing. It validates each message exactly like a real provider (invalid messages fail),
/// records every message, and answers with <see cref="Responder"/>. Thread-safe.
/// </summary>
/// <typeparam name="TMessage">The message type.</typeparam>
public class FakeNotifier<TMessage> : INotifier<TMessage> where TMessage : NotifierMessage
{
    private readonly ConcurrentQueue<TMessage> _sent = new();

    /// <summary>Every message passed to <see cref="SendAsync"/>, in order, including invalid ones.</summary>
    public IReadOnlyList<TMessage> Sent => [.. _sent];

    /// <summary>Decides the response for a valid message. Defaults to an accepted response. Set it to simulate provider failures.</summary>
    public Func<TMessage, NotifierResponse> Responder { get; set; } = static _ => NotifierResponse.Accepted(Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public Task<NotifierResponse> SendAsync(TMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        _sent.Enqueue(message);
        var errors = message.Validate();
        return Task.FromResult(errors.Count > 0 ? NotifierResponse.Failed(errors) : Responder(message));
    }

    /// <summary>Forgets the recorded messages.</summary>
    public void Clear() => _sent.Clear();
}

/// <summary>A fake <see cref="IEmailNotifier"/>.</summary>
public sealed class FakeEmailNotifier : FakeNotifier<EmailMessage>, IEmailNotifier;

/// <summary>A fake <see cref="ISmsNotifier"/>.</summary>
public sealed class FakeSmsNotifier : FakeNotifier<SmsMessage>, ISmsNotifier;

/// <summary>A fake <see cref="IPushNotifier"/>.</summary>
public sealed class FakePushNotifier : FakeNotifier<PushMessage>, IPushNotifier;

/// <summary>A fake <see cref="ITelegramNotifier"/>.</summary>
public sealed class FakeTelegramNotifier : FakeNotifier<TelegramMessage>, ITelegramNotifier;

/// <summary>A fake <see cref="IWebPushNotifier"/>.</summary>
public sealed class FakeWebPushNotifier : FakeNotifier<WebPushMessage>, IWebPushNotifier;

/// <summary>Registers the fake notifiers.</summary>
public static class FakeNotifierBuilderExtensions
{
    /// <summary>
    /// Registers a fake notifier for every channel as the default provider. Resolve the concrete fake
    /// (<see cref="FakeEmailNotifier"/> and so on) from the service provider to inspect what was sent.
    /// </summary>
    public static INotifierBuilder AddFakeNotifiers(this INotifierBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<FakeEmailNotifier>();
        builder.Services.AddSingleton<FakeSmsNotifier>();
        builder.Services.AddSingleton<FakePushNotifier>();
        builder.Services.AddSingleton<FakeTelegramNotifier>();
        builder.Services.AddSingleton<FakeWebPushNotifier>();

        return builder
            .AddNotifier<IEmailNotifier>("fake", null, static (services, _) => services.GetRequiredService<FakeEmailNotifier>())
            .AddNotifier<ISmsNotifier>("fake", null, static (services, _) => services.GetRequiredService<FakeSmsNotifier>())
            .AddNotifier<IPushNotifier>("fake", null, static (services, _) => services.GetRequiredService<FakePushNotifier>())
            .AddNotifier<ITelegramNotifier>("fake", null, static (services, _) => services.GetRequiredService<FakeTelegramNotifier>())
            .AddNotifier<IWebPushNotifier>("fake", null, static (services, _) => services.GetRequiredService<FakeWebPushNotifier>());
    }
}
