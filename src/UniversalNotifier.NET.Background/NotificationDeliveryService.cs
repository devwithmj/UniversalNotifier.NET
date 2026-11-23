using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace UniversalNotifier.NET.Background;

/// <summary>
/// Delivers queued notifications with a fixed number of concurrent workers. On shutdown it stops accepting new
/// notifications and delivers what is already queued until the host's shutdown timeout runs out; anything still queued
/// then is lost with an in-memory queue, which is why a durable queue matters when that is not acceptable.
/// </summary>
internal sealed partial class NotificationDeliveryService : IHostedService, IDisposable
{
    private readonly INotificationQueue _queue;
    private readonly INotificationService _service;
    private readonly INotificationResultHandler[] _handlers;
    private readonly ILogger _logger;
    private readonly int _concurrency;
    private readonly CancellationTokenSource _abort = new();
    private Task[] _workers = [];

    public NotificationDeliveryService(
        INotificationQueue queue,
        INotificationService service,
        IEnumerable<INotificationResultHandler> handlers,
        IOptions<BackgroundDeliveryOptions> options,
        ILoggerFactory? loggerFactory = null)
    {
        _queue = queue;
        _service = service;
        _handlers = [.. handlers];
        _concurrency = options.Value.MaxConcurrency;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<NotificationDeliveryService>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _workers = [.. Enumerable.Range(0, _concurrency).Select(_ => Task.Run(RunWorkerAsync, CancellationToken.None))];
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Complete();
        try
        {
            await Task.WhenAll(_workers).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The shutdown timeout ran out: cancel in-flight sends so the host can stop.
            await _abort.CancelAsync().ConfigureAwait(false);
        }
    }

    public void Dispose() => _abort.Dispose();

    private async Task RunWorkerAsync()
    {
        try
        {
            await foreach (var notification in _queue.DequeueAllAsync(_abort.Token).ConfigureAwait(false))
            {
                await DeliverAsync(notification).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            // forced shutdown
        }
    }

    private async Task DeliverAsync(Notification notification)
    {
        NotificationResult result;
        try
        {
            result = await _service.SendAsync(notification, _abort.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A notification that cannot even be processed (for example one with no channels) must not stop the worker.
            LogUnprocessable(_logger, notification.Id ?? "-", exception);
            return;
        }

        if (result.Success)
        {
            LogDelivered(_logger, notification.Id ?? "-");
        }
        else
        {
            LogNotDelivered(_logger, notification.Id ?? "-", result.Status, string.Join(',', result.Channels.Where(c => !c.Response.Success).Select(c => c.Channel.Name)));
        }

        foreach (var handler in _handlers)
        {
            try
            {
                await handler.HandleAsync(notification, result, _abort.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_abort.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogHandlerFailed(_logger, notification.Id ?? "-", exception);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Background notification {NotificationId} accepted by all channels")]
    private static partial void LogDelivered(ILogger logger, string notificationId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Background notification {NotificationId} {Status}; failed channels: {Channels}")]
    private static partial void LogNotDelivered(ILogger logger, string notificationId, DeliveryStatus status, string channels);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Background notification {NotificationId} could not be processed and was dropped")]
    private static partial void LogUnprocessable(ILogger logger, string notificationId, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "A result handler failed for background notification {NotificationId}")]
    private static partial void LogHandlerFailed(ILogger logger, string notificationId, Exception exception);
}
