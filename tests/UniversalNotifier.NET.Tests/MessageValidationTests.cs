namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class MessageValidationTests
{
    [TestCase("user@example.com", true)]
    [TestCase("first.last+tag@sub.example.co.uk", true)]
    [TestCase("ünï@exämple.com", true)]
    [TestCase("", false)]
    [TestCase("no-at-sign", false)]
    [TestCase("two@@example.com", false)]
    [TestCase("a@b@example.com", false)]
    [TestCase("user@localhost", false)]
    [TestCase("user @example.com", false)]
    [TestCase("user@example.com\r\nBcc: evil@example.com", false)]
    [TestCase("<user@example.com>", false)]
    [TestCase("a,b@example.com", false)]
    [TestCase(".user@example.com", false)]
    [TestCase("us..er@example.com", false)]
    [TestCase("user@example..com", false)]
    [TestCase("user@.example.com", false)]
    public void Email_address_validation(string address, bool valid) =>
        Assert.That(new EmailAddress(address).IsValid, Is.EqualTo(valid));

    [Test]
    public void Email_address_with_default_value_is_invalid() =>
        Assert.That(default(EmailAddress).IsValid, Is.False);

    [Test]
    public void Display_name_is_quoted_so_commas_and_quotes_cannot_split_recipients()
    {
        var address = new EmailAddress("a@example.com", "Smith, \"Bob\" <x@evil.com>");

        Assert.That(address.ToString(), Is.EqualTo("\"Smith, \\\"Bob\\\" <x@evil.com>\" <a@example.com>"));
    }

    [Test]
    public void Display_name_with_line_break_is_invalid() =>
        Assert.That(new EmailAddress("a@example.com", "Bob\r\nBcc: x@example.com").IsValid, Is.False);

    [Test]
    public void Address_without_a_name_formats_as_the_bare_address() =>
        Assert.That(new EmailAddress("a@example.com").ToString(), Is.EqualTo("a@example.com"));

    [TestCase("+14165551234", true)]
    [TestCase("+442071838750", true)]
    [TestCase("14165551234", false)]
    [TestCase("+0123456789", false)]
    [TestCase("+1234", false)]
    [TestCase("+1416555123a", false)]
    [TestCase("+1234567890123456", false)]
    [TestCase("", false)]
    public void Phone_number_validation(string number, bool valid) =>
        Assert.That(new PhoneNumber(number).IsValid, Is.EqualTo(valid));

    [Test]
    public void A_valid_email_has_no_errors() =>
        Assert.That(EmailMessage.Create("a@example.com", "Hi", "Body").Validate(), Is.Empty);

    [Test]
    public void An_email_reports_every_problem_without_echoing_addresses()
    {
        var message = new EmailMessage
        {
            To = ["not-an-address"],
            Subject = "line\nbreak",
            Headers = new Dictionary<string, string> { ["Bad Name"] = "x" },
        };

        var errors = message.Validate();

        Assert.That(errors.Select(e => e.Code), Is.EquivalentTo(new[]
        {
            "email.address.invalid", "email.subject.invalid", "email.body.required", "email.header.invalid",
        }));
        Assert.That(errors.All(e => e.Kind == NotifierErrorKind.Validation && !e.Message.Contains("not-an-address", StringComparison.Ordinal)));
    }

    [Test]
    public void An_email_needs_a_recipient_in_to_cc_or_bcc()
    {
        var none = new EmailMessage { Subject = "s", TextBody = "b" };
        var bccOnly = new EmailMessage { Bcc = ["a@example.com"], Subject = "s", TextBody = "b" };

        Assert.That(none.Validate().Select(e => e.Code), Does.Contain("email.recipients.required"));
        Assert.That(bccOnly.Validate(), Is.Empty);
    }

    [Test]
    public void Cc_and_bcc_are_only_allowed_with_a_single_recipient()
    {
        var bulk = new EmailMessage { To = ["a@example.com", "b@example.com"], Cc = ["c@example.com"], Subject = "s", TextBody = "b" };
        var single = new EmailMessage { To = ["a@example.com"], Cc = ["c@example.com"], Bcc = ["d@example.com"], Subject = "s", TextBody = "b" };

        Assert.That(bulk.Validate().Select(e => e.Code), Does.Contain("email.copies.bulk"));
        Assert.That(single.Validate(), Is.Empty);
    }

    [Test]
    public void Email_attachments_are_validated()
    {
        var bad = EmailMessage.Create("a@example.com", "s", "b") with
        {
            Attachments = [new EmailAttachment("../etc/passwd", new byte[] { 1 })],
        };
        var good = EmailMessage.Create("a@example.com", "s", "b") with
        {
            Attachments = [new EmailAttachment("report.pdf", new byte[] { 1 }, "application/pdf")],
        };

        Assert.That(bad.Validate().Select(e => e.Code), Does.Contain("email.attachment.invalid"));
        Assert.That(good.Validate(), Is.Empty);
    }

    [Test]
    public void Sms_validation()
    {
        Assert.That(SmsMessage.Create("+14165551234", "Hi").Validate(), Is.Empty);
        Assert.That(SmsMessage.Create("4165551234", " ").Validate().Select(e => e.Code),
            Is.EquivalentTo(new[] { "sms.number.invalid", "sms.body.required" }));
        Assert.That(new SmsMessage { Body = "Hi" }.Validate().Select(e => e.Code), Does.Contain("sms.recipients.required"));
    }

    [Test]
    public void Push_validation()
    {
        Assert.That(PushMessage.Create("token", "T", "B").Validate(), Is.Empty);
        Assert.That(new PushMessage { Topic = "news", Data = new Dictionary<string, string> { ["k"] = "v" } }.Validate(), Is.Empty);
        Assert.That(new PushMessage { Title = "T" }.Validate().Select(e => e.Code), Does.Contain("push.target.required"));
        Assert.That(new PushMessage { Tokens = ["t"] }.Validate().Select(e => e.Code), Does.Contain("push.content.required"));
    }

    [Test]
    public void Telegram_validation()
    {
        Assert.That(TelegramMessage.Create("123", "Hi").Validate(), Is.Empty);
        Assert.That(new TelegramMessage().Validate().Select(e => e.Code),
            Is.EquivalentTo(new[] { "telegram.chat.required", "telegram.text.required" }));
    }

    [Test]
    public void WebPush_validation()
    {
        var good = new WebPushSubscription("https://push.example.com/abc", "key", "auth");
        var insecure = new WebPushSubscription("http://push.example.com/abc", "key", "auth");

        Assert.That(new WebPushMessage { Subscriptions = [good], Title = "T" }.Validate(), Is.Empty);
        Assert.That(new WebPushMessage { Subscriptions = [insecure], Title = "T" }.Validate().Select(e => e.Code),
            Does.Contain("webpush.subscription.invalid"));
        Assert.That(new WebPushMessage { Subscriptions = [good] }.Validate().Select(e => e.Code),
            Does.Contain("webpush.content.required"));
    }

    [Test]
    public void ToString_never_prints_recipients_or_content()
    {
        var message = EmailMessage.Create("secret@example.com", "private subject", "private body") with { Id = "m1" };

        Assert.That(message.ToString(), Is.EqualTo("EmailMessage { Id = m1 }"));
        Assert.That(Notification.To("secret@example.com").ToString(), Is.EqualTo("Notification { Id = - }"));
        Assert.That(Recipient.Parse("secret@example.com").ToString(), Is.EqualTo("Recipient"));
    }

    [Test]
    public void Response_status_is_derived_from_recipient_results()
    {
        var ok = new RecipientResult("a", true);
        var bad = new RecipientResult("b", false, Error: new NotifierError(NotifierErrorKind.Permanent, "x", "rejected"));

        Assert.That(NotifierResponse.FromRecipients([ok, ok]).Status, Is.EqualTo(DeliveryStatus.Accepted));
        Assert.That(NotifierResponse.FromRecipients([ok, bad]).Status, Is.EqualTo(DeliveryStatus.PartiallyAccepted));
        Assert.That(NotifierResponse.FromRecipients([bad]).Status, Is.EqualTo(DeliveryStatus.Failed));
        Assert.That(NotifierResponse.FromRecipients([]).Status, Is.EqualTo(DeliveryStatus.Failed));
        Assert.That(NotifierResponse.FromRecipients([ok, bad]).Errors, Has.Count.EqualTo(1));
        Assert.That(NotifierResponse.FromRecipients([ok]).Success, Is.True);
    }
}
