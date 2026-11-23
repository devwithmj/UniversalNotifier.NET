// Background delivery in a Worker Service: producers queue notifications and return immediately,
// a hosted worker delivers them, and a result handler sees every outcome.
//
// This sample uses fake notifiers so it runs without credentials. Replace AddFakeNotifiers() with
// AddSmtp(...), AddMailgun(...), AddTwilio(...) and so on for real delivery.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Background;
using UniversalNotifier.NET.Testing;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddUniversalNotifier(n => n
    .AddFakeNotifiers()
    .WithRetries(maxAttempts: 3) // retries only failures that are safe to repeat
    .AddBackgroundDelivery(o =>
    {
        o.Capacity = 500;      // a full queue slows producers down instead of growing memory
        o.MaxConcurrency = 8;  // notifications delivered at the same time
    }));

builder.Services.AddSingleton<INotificationResultHandler, PrintResults>();
builder.Services.AddHostedService<Producer>();

await builder.Build().RunAsync();

internal sealed class Producer(INotificationQueue queue, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var i = 1; i <= 5; i++)
        {
            var accepted = await queue.EnqueueAsync(
                Notification.To($"user{i}@example.com")
                    .Email("Order {{Number}} shipped", text: "Your order {{Number}} is on its way.")
                    .WithData(new { Number = 1000 + i }),
                stoppingToken);
            Console.WriteLine($"Queued #{i}: {accepted}");
        }

        await Task.Delay(500, stoppingToken); // give the worker a moment; shutdown would also drain the queue
        lifetime.StopApplication();
    }
}

internal sealed class PrintResults : INotificationResultHandler
{
    public ValueTask HandleAsync(Notification notification, NotificationResult result, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Delivered a notification: {result.Status}");
        return ValueTask.CompletedTask;
    }
}
