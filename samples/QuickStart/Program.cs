// Sends one notification without a host or dependency injection.
//
//   dotnet run
//       uses fake notifiers and prints what would be sent
//   SMTP_HOST=smtp.example.com SMTP_USER=... SMTP_PASSWORD=... SMTP_FROM=noreply@example.com dotnet run
//       sends a real email

using Microsoft.Extensions.DependencyInjection; // provider Add... methods (AddSmtp, AddMailgun, ...) live here, as with other builder extensions
using UniversalNotifier.NET;
using UniversalNotifier.NET.Testing;

var builder = new NotifierBuilder();

if (Environment.GetEnvironmentVariable("SMTP_HOST") is { Length: > 0 } host)
{
    builder.AddSmtp(o =>
    {
        o.Host = host;
        o.UserName = Environment.GetEnvironmentVariable("SMTP_USER");
        o.Password = Environment.GetEnvironmentVariable("SMTP_PASSWORD");
        o.From = Environment.GetEnvironmentVariable("SMTP_FROM") ?? "noreply@example.com";
    });
}
else
{
    builder.AddFakeNotifiers();
}

await using var notifier = builder.Build();

var result = await notifier.SendAsync(
    Notification.To("ada@example.com")
        .Email(
            "Welcome, {{Name}}!",
            html: "<p>Hi <b>{{Name}}</b>, thanks for joining {{Company}}.</p>",
            text: "Hi {{Name}}, thanks for joining {{Company}}.")
        .WithData(new { Name = "Ada", Company = "Example" }));

Console.WriteLine($"Accepted: {result.Success} ({result.Status})");
foreach (var channel in result.Channels)
{
    Console.WriteLine($"  {channel.Channel}: {channel.Response.Status} {string.Join(", ", channel.Response.Errors)}");
}

if (notifier.GetNotifier<IEmailNotifier>() is FakeEmailNotifier fake)
{
    var sent = fake.Sent.Single();
    Console.WriteLine($"Would send \"{sent.Subject}\" to {sent.To[0]}:\n{sent.TextBody}");
}
