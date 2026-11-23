#pragma warning disable CS0618 // Message.Token holds registration tokens; see FcmPushNotifier.
using System.Security.Cryptography;
using System.Text.Json;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET.Push.Fcm;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class FcmPushNotifierTests
{
    private sealed class FakeGateway(Func<IReadOnlyList<Message>, IReadOnlyList<FcmResult>>? respond = null) : IFcmGateway
    {
        public List<IReadOnlyList<Message>> Batches { get; } = [];

        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<FcmResult>> SendEachAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
        {
            Batches.Add(messages);
            return Task.FromResult(respond?.Invoke(messages) ?? [.. messages.Select((_, i) => new FcmResult("projects/p/messages/" + i, null))]);
        }

        public void Dispose() => Disposed = true;
    }

    private static FcmPushNotifier Create(FakeGateway gateway) => new(new FcmPushOptions(), gateway, new NotifierContext());

    private static PushMessage Push(params string[] tokens) => new() { Tokens = tokens, Title = "Hi", Body = "There" };

    [Test]
    public async Task Sends_one_message_per_token_with_notification_and_data()
    {
        var gateway = new FakeGateway();
        var message = Push("t1", "t2") with { Data = new Dictionary<string, string> { ["order"] = "42" } };

        var response = await Create(gateway).SendAsync(message);

        Assert.That(response.Success, Is.True);
        var sent = gateway.Batches.Single();
        Assert.That(sent.Select(m => m.Token), Is.EqualTo(new[] { "t1", "t2" }));
        Assert.That(sent[0].Notification.Title, Is.EqualTo("Hi"));
        Assert.That(sent[0].Notification.Body, Is.EqualTo("There"));
        Assert.That(sent[0].Data["order"], Is.EqualTo("42"));
        Assert.That(response.Recipients.Select(r => r.Recipient), Is.EqualTo(new[] { "t1", "t2" }));
        Assert.That(response.Recipients.All(r => r.ProviderMessageId!.StartsWith("projects/", StringComparison.Ordinal)));
    }

    [Test]
    public async Task A_data_only_message_has_no_visible_notification()
    {
        var gateway = new FakeGateway();

        await Create(gateway).SendAsync(new PushMessage { Tokens = ["t"], Data = new Dictionary<string, string> { ["k"] = "v" } });

        Assert.That(gateway.Batches.Single()[0].Notification, Is.Null);
    }

    [Test]
    public async Task Unregistered_tokens_are_reported_per_token_so_they_can_be_removed()
    {
        var gateway = new FakeGateway(m => [.. m.Select(x => x.Token == "dead"
            ? new FcmResult(null, MessagingErrorCode.Unregistered)
            : new FcmResult("ok", null))]);

        var response = await Create(gateway).SendAsync(Push("live", "dead", "live2"));

        Assert.That(response.Status, Is.EqualTo(DeliveryStatus.PartiallyAccepted));
        var dead = response.Recipients.Single(r => !r.Accepted);
        Assert.That(dead.Recipient, Is.EqualTo("dead"));
        Assert.That(dead.Error!.Value.Code, Is.EqualTo("fcm.unregistered"));
        Assert.That(dead.Error.Value.Kind, Is.EqualTo(NotifierErrorKind.Permanent));
    }

    [TestCase(MessagingErrorCode.InvalidArgument, NotifierErrorKind.Permanent, "fcm.invalid_argument")]
    [TestCase(MessagingErrorCode.SenderIdMismatch, NotifierErrorKind.Configuration, "fcm.unauthorized")]
    [TestCase(MessagingErrorCode.ThirdPartyAuthError, NotifierErrorKind.Configuration, "fcm.unauthorized")]
    [TestCase(MessagingErrorCode.QuotaExceeded, NotifierErrorKind.Transient, "fcm.quota_exceeded")]
    [TestCase(MessagingErrorCode.Unavailable, NotifierErrorKind.Transient, "fcm.unavailable")]
    [TestCase(MessagingErrorCode.Internal, NotifierErrorKind.Unknown, "fcm.error")]
    public async Task Error_codes_are_classified(MessagingErrorCode code, NotifierErrorKind kind, string expected)
    {
        var gateway = new FakeGateway(m => [.. m.Select(_ => new FcmResult(null, code))]);

        var response = await Create(gateway).SendAsync(Push("t"));

        Assert.That(response.Status, Is.EqualTo(DeliveryStatus.Failed));
        Assert.That(response.Errors[0].Kind, Is.EqualTo(kind));
        Assert.That(response.Errors[0].Code, Is.EqualTo(expected));
    }

    [Test]
    public async Task A_topic_is_sent_as_its_own_message()
    {
        var gateway = new FakeGateway();

        var response = await Create(gateway).SendAsync(new PushMessage { Tokens = ["t"], Topic = "news", Title = "Hi" });

        Assert.That(gateway.Batches.Single().Select(m => (m.Token, m.Topic)), Is.EqualTo(new[] { ("t", (string?)null), ((string?)null, "news") }));
        Assert.That(response.Recipients.Select(r => r.Recipient), Is.EqualTo(new[] { "t", "topic:news" }));
    }

    [Test]
    public async Task More_than_500_targets_are_sent_in_batches_of_500()
    {
        var gateway = new FakeGateway();
        var tokens = Enumerable.Range(0, 1100).Select(i => "token" + i).ToArray();

        var response = await Create(gateway).SendAsync(Push(tokens));

        Assert.That(gateway.Batches.Select(b => b.Count), Is.EqualTo(new[] { 500, 500, 100 }));
        Assert.That(response.Recipients, Has.Count.EqualTo(1100));
        Assert.That(response.Recipients.Select(r => r.Recipient), Is.EqualTo(tokens));
    }

    [Test]
    public async Task An_sdk_failure_becomes_a_failed_response()
    {
        var gateway = new FakeGateway(_ => throw new InvalidOperationException("sdk exploded"));

        var response = await Create(gateway).SendAsync(Push("t"));

        Assert.That(response.Success, Is.False);
        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Unknown));
    }

    [Test]
    public async Task Invalid_messages_never_reach_firebase()
    {
        var gateway = new FakeGateway();

        var response = await Create(gateway).SendAsync(new PushMessage());

        Assert.That(response.Success, Is.False);
        Assert.That(gateway.Batches, Is.Empty);
    }

    [Test]
    public void Disposing_the_notifier_releases_the_sdk_app()
    {
        var gateway = new FakeGateway();

        Create(gateway).Dispose();

        Assert.That(gateway.Disposed, Is.True);
    }

    [Test]
    public void Conflicting_credential_options_are_rejected() =>
        Assert.Throws<ArgumentException>(() => new FcmPushNotifier(new FcmPushOptions { CredentialJson = "{}", CredentialPath = "k.json" }, new NotifierContext()));

    [Test]
    public void Options_never_print_credentials() =>
        Assert.That(new FcmPushOptions { CredentialJson = "SECRET-KEY", ProjectId = "p" }.ToString(), Does.Not.Contain("SECRET"));

    [Test]
    public void It_registers_through_dependency_injection_with_service_account_json_and_no_network()
    {
        using var rsa = RSA.Create(2048);
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account",
            ["project_id"] = "demo-project",
            ["private_key_id"] = "key-id",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["client_email"] = "sender@demo-project.iam.gserviceaccount.com",
            ["client_id"] = "1",
            ["token_uri"] = "https://oauth2.googleapis.com/token",
        });

        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddFcm(o => o.CredentialJson = json))
            .BuildServiceProvider();

        Assert.That(provider.GetRequiredService<IPushNotifier>(), Is.InstanceOf<FcmPushNotifier>());
        Assert.That(provider.GetRequiredKeyedService<IPushNotifier>("fcm"), Is.SameAs(provider.GetRequiredService<IPushNotifier>()));
    }

    [Test]
    public void Invalid_settings_are_reported_by_the_options_system()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddFcm(o => { o.CredentialJson = "{}"; o.CredentialPath = "k.json"; }))
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IPushNotifier>());
    }
}
