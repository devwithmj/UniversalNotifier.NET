using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using UniversalNotifier.NET.InApp;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class InAppNotifierTests
{
    private static ServiceProvider Build(Action<InAppOptions>? configure = null) =>
        new ServiceCollection().AddUniversalNotifier(n => n.AddInApp(configure)).BuildServiceProvider();

    [Test]
    public async Task A_stored_notification_appears_in_the_users_inbox_unread_and_newest_first()
    {
        using var provider = Build();
        var notifier = provider.GetRequiredService<IInAppNotifier>();
        var store = provider.GetRequiredService<IInAppStore>();

        var first = await notifier.SendAsync(InAppMessage.Create("u1", "First", "Welcome"));
        await notifier.SendAsync(new InAppMessage { UserId = "u1", Text = "Second", Data = new Dictionary<string, string> { ["url"] = "/orders/7" } });
        await notifier.SendAsync(InAppMessage.Create("u2", "Someone else"));

        var inbox = await store.ListAsync("u1");
        Assert.That(first.Success, Is.True);
        Assert.That(inbox.Select(n => n.Text), Is.EqualTo(new[] { "Second", "First" }));
        Assert.That(inbox[1].Title, Is.EqualTo("Welcome"));
        Assert.That(inbox[0].Data!["url"], Is.EqualTo("/orders/7"));
        Assert.That(inbox.All(n => !n.IsRead));
        Assert.That(inbox[1].Id, Is.EqualTo(first.ProviderMessageId));
        Assert.That(await store.CountUnreadAsync("u1"), Is.EqualTo(2));
        Assert.That(await store.CountUnreadAsync("u2"), Is.EqualTo(1));
        Assert.That(await store.CountUnreadAsync("nobody"), Is.Zero);
    }

    [Test]
    public async Task Marking_read_updates_counts_and_filters()
    {
        using var provider = Build();
        var notifier = provider.GetRequiredService<IInAppNotifier>();
        var store = provider.GetRequiredService<IInAppStore>();
        var a = (await notifier.SendAsync(InAppMessage.Create("u1", "a"))).ProviderMessageId!;
        await notifier.SendAsync(InAppMessage.Create("u1", "b"));

        Assert.That(await store.MarkReadAsync("u1", a), Is.True);
        Assert.That(await store.MarkReadAsync("u1", "missing"), Is.False);
        Assert.That(await store.MarkReadAsync("u2", a), Is.False, "another user cannot mark it");

        Assert.That(await store.CountUnreadAsync("u1"), Is.EqualTo(1));
        Assert.That((await store.ListAsync("u1", unreadOnly: true)).Select(n => n.Text), Is.EqualTo(new[] { "b" }));

        await store.MarkAllReadAsync("u1");
        Assert.That(await store.CountUnreadAsync("u1"), Is.Zero);
        Assert.That(await store.ListAsync("u1", unreadOnly: true), Is.Empty);
    }

    [Test]
    public async Task The_store_is_bounded_per_user_and_limits_what_it_lists()
    {
        using var provider = Build(o => o.MaxPerUser = 3);
        var notifier = provider.GetRequiredService<IInAppNotifier>();
        var store = provider.GetRequiredService<IInAppStore>();

        for (var i = 1; i <= 5; i++)
        {
            await notifier.SendAsync(InAppMessage.Create("u1", "m" + i));
        }

        Assert.That((await store.ListAsync("u1")).Select(n => n.Text), Is.EqualTo(new[] { "m5", "m4", "m3" }));
        Assert.That(await store.ListAsync("u1", limit: 2), Has.Count.EqualTo(2));
        Assert.That(await store.ListAsync("u1", limit: 0), Is.Empty);
    }

    [Test]
    public async Task The_creation_time_comes_from_the_injected_clock()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var store = new InMemoryInAppStore();
        var notifier = new InAppNotifier(store, new NotifierContext(timeProvider: time));

        await notifier.SendAsync(InAppMessage.Create("u1", "x"));

        Assert.That((await store.ListAsync("u1"))[0].CreatedAt, Is.EqualTo(time.GetUtcNow()));
    }

    [Test]
    public async Task Invalid_messages_are_rejected_and_nothing_is_stored()
    {
        using var provider = Build();

        var response = await provider.GetRequiredService<IInAppNotifier>().SendAsync(new InAppMessage());

        Assert.That(response.Success, Is.False);
        Assert.That(response.Errors.Select(e => e.Code), Is.EquivalentTo(new[] { "inapp.user.required", "inapp.text.required" }));
    }

    private sealed class FailingStore : IInAppStore
    {
        public ValueTask AddAsync(InAppNotification notification, CancellationToken cancellationToken = default) => throw new InvalidOperationException("database down");

        public ValueTask<IReadOnlyList<InAppNotification>> ListAsync(string userId, bool unreadOnly = false, int limit = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<int> CountUnreadAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<bool> MarkReadAsync(string userId, string notificationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask MarkAllReadAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Test]
    public async Task A_custom_store_registered_first_is_used_and_its_failures_become_results()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IInAppStore, FailingStore>()
            .AddUniversalNotifier(n => n.AddInApp())
            .BuildServiceProvider();

        var response = await provider.GetRequiredService<IInAppNotifier>().SendAsync(InAppMessage.Create("u1", "x"));

        Assert.That(provider.GetRequiredService<IInAppStore>(), Is.InstanceOf<FailingStore>());
        Assert.That(response.Success, Is.False);
        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Unknown));
    }

    [Test]
    public async Task It_works_through_the_dispatcher_with_templates_and_other_channels()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddInApp().AddTemplate("shipped", NotificationChannel.InApp, new ChannelContent { Title = "Order {{No}}", Text = "Order {{No}} shipped" }))
            .BuildServiceProvider();

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To(new Recipient { UserId = "u9", Email = "ada@example.com" }).InApp().Email("Hi", text: "x").UseTemplate("shipped").WithData(new { No = 42 }));

        Assert.That(result[NotificationChannel.InApp]!.Success, Is.True);
        Assert.That(result[NotificationChannel.Email]!.Errors[0].Code, Is.EqualTo("email.provider.missing"));
        var entry = (await provider.GetRequiredService<IInAppStore>().ListAsync("u9")).Single();
        Assert.That(entry.Title, Is.EqualTo("Order 42"));
        Assert.That(entry.Text, Is.EqualTo("Order 42 shipped"));
    }

    [Test]
    public async Task A_recipient_without_a_user_id_fails_only_this_channel()
    {
        using var provider = Build();

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(Notification.To("ada@example.com").InApp("T", "x"));

        Assert.That(result[NotificationChannel.InApp]!.Errors[0].Code, Is.EqualTo("inapp.address.missing"));
    }

    [Test]
    public async Task Concurrent_writes_and_reads_are_safe()
    {
        using var provider = Build(o => o.MaxPerUser = 10_000);
        var notifier = provider.GetRequiredService<IInAppNotifier>();
        var store = provider.GetRequiredService<IInAppStore>();

        await Task.WhenAll(Enumerable.Range(0, 500).Select(i => Task.Run(async () =>
        {
            await notifier.SendAsync(InAppMessage.Create("u1", "m" + i));
            await store.CountUnreadAsync("u1");
            await store.ListAsync("u1", limit: 5);
        })));

        Assert.That(await store.CountUnreadAsync("u1"), Is.EqualTo(500));
    }

    [Test]
    public void Invalid_settings_are_rejected()
    {
        using var provider = Build(o => o.MaxPerUser = 0);

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IInAppStore>());
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryInAppStore(0));
    }
}
