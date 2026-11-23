// One notification, four channels, templates with logic (Scriban), per-channel results, retries and rate limiting.
// Uses fake notifiers so it runs without credentials; swap AddFakeNotifiers() for AddSmtp(), AddTwilioSms(), AddFcm(), ...

using Microsoft.Extensions.DependencyInjection;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Testing;

var services = new ServiceCollection();
services.AddUniversalNotifier(n => n
    .AddFakeNotifiers()
    .UseScriban()
    .WithRetries(maxAttempts: 3)
    .WithRateLimit(permitsPerSecond: 50, burst: 10)
    .AddTemplate("appointment", NotificationChannel.Email, new ChannelContent
    {
        Subject = "Reminder: {{ Service }} on {{ Date }}",
        Html = "<p>Hello {{ Name }},</p>{{ if Remote }}<p>Join at {{ Link }}</p>{{ else }}<p>See you at our office.</p>{{ end }}",
        Text = "Hello {{ Name }}, your {{ Service }} is on {{ Date }}.",
    })
    .AddTemplate("appointment", NotificationChannel.Sms, new ChannelContent { Text = "{{ Name }}: {{ Service }} on {{ Date }}." })
    .AddTemplate("appointment", NotificationChannel.Push, new ChannelContent { Title = "Appointment", Text = "{{ Service }} on {{ Date }}" }));

await using var provider = services.BuildServiceProvider();

var patient = new Recipient
{
    Email = "ada@example.com",
    Phone = "+14165551234",
    DeviceTokens = ["device-token-1"],
    // no TelegramChatId on purpose: that channel reports a clear per-channel error without blocking the others
};

var result = await provider.GetRequiredService<INotificationService>().SendAsync(
    Notification.To(patient)
        .Email().Sms().Push().Telegram("Reminder for {{ Name }}")
        .UseTemplate("appointment")
        .WithData(new { Name = "Ada", Service = "check-up", Date = "Monday 9:00", Remote = true, Link = "https://example.com/room/42" }));

Console.WriteLine($"Overall: {result.Status}");
foreach (var (channel, response) in result.Channels)
{
    Console.WriteLine($"  {channel,-9} {response.Status,-17} {string.Join("; ", response.Errors)}");
}

var email = provider.GetRequiredService<FakeEmailNotifier>().Sent.Single();
Console.WriteLine($"\nEmail subject: {email.Subject}\nEmail html:    {email.HtmlBody}");
