using System.Diagnostics;

namespace UniversalNotifier.NET;

internal sealed class NotificationService : INotificationService
{
    private readonly Dictionary<NotificationChannel, INotificationChannel> _channels = [];
    private readonly ITemplateStore[] _stores;
    private readonly ITemplateRenderer _renderer;

    public NotificationService(IEnumerable<INotificationChannel> channels, IEnumerable<ITemplateStore> stores, ITemplateRenderer renderer)
    {
        foreach (var channel in channels)
        {
            _channels[channel.Channel] = channel; // a later registration replaces an earlier one, so users can override built-ins
        }

        _stores = [.. stores];
        _renderer = renderer;
    }

    public async Task<NotificationResult> SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.Channels.Count == 0)
        {
            throw new ArgumentException("A notification needs at least one channel, for example .Email() or .Sms().", nameof(notification));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var data = TemplateData.From(notification.Data);

        using var activity = NotifierTelemetry.Source.StartActivity("notify", ActivityKind.Internal);
        activity?.SetTag("notifier.channels", notification.Channels.Count);

        NotificationResult result;
        if (notification.Channels.Count == 1)
        {
            var single = await SendChannelAsync(notification, notification.Channels[0], data, cancellationToken).ConfigureAwait(false);
            result = new NotificationResult(notification.Id, [single]);
        }
        else
        {
            var tasks = new Task<ChannelResult>[notification.Channels.Count];
            for (var i = 0; i < tasks.Length; i++)
            {
                tasks[i] = SendChannelAsync(notification, notification.Channels[i], data, cancellationToken);
            }

            result = new NotificationResult(notification.Id, await Task.WhenAll(tasks).ConfigureAwait(false));
        }

        NotifierTelemetry.Notifications.Add(1, new KeyValuePair<string, object?>("status", result.Status.ToString()));
        if (activity is not null)
        {
            activity.SetTag("notifier.status", result.Status.ToString());
            if (!result.Success)
            {
                activity.SetStatus(ActivityStatusCode.Error, result.Status.ToString());
            }
        }

        return result;
    }

    private async Task<ChannelResult> SendChannelAsync(Notification notification, ChannelRequest request, TemplateData data, CancellationToken cancellationToken)
    {
        NotifierResponse response;
        try
        {
            response = await DeliverAsync(notification, request, data, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TemplateRenderException exception)
        {
            response = NotifierResponse.Failed(new NotifierError(NotifierErrorKind.Validation, "template.render", exception.Message));
        }
        catch (Exception exception)
        {
            response = NotifierResponse.Failed(new NotifierError(NotifierErrorKind.Unknown, "channel.error", $"{exception.GetType().Name}: {exception.Message}"));
        }

        return new ChannelResult(request.Channel, response);
    }

    private async Task<NotifierResponse> DeliverAsync(Notification notification, ChannelRequest request, TemplateData data, CancellationToken cancellationToken)
    {
        if (!_channels.TryGetValue(request.Channel, out var handler))
        {
            return NotifierResponse.Failed(new NotifierError(
                NotifierErrorKind.Configuration, $"{request.Channel}.channel.unknown", $"No handler is registered for the '{request.Channel}' channel."));
        }

        var content = await ResolveContentAsync(notification, request, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return NotifierResponse.Failed(new NotifierError(
                NotifierErrorKind.Validation, $"{request.Channel}.content.missing",
                $"No content for the '{request.Channel}' channel: pass inline content or use a template that defines it."));
        }

        return await handler.SendAsync(new NotificationDelivery(notification, Render(content, data), request.Provider), cancellationToken).ConfigureAwait(false);
    }

    // Inline parts win; the template fills whatever the inline content leaves out.
    private async ValueTask<ChannelContent?> ResolveContentAsync(Notification notification, ChannelRequest request, CancellationToken cancellationToken)
    {
        ChannelContent? fromTemplate = null;
        if (notification.TemplateName is { } name)
        {
            foreach (var store in _stores)
            {
                fromTemplate = await store.TryGetAsync(name, request.Channel, cancellationToken).ConfigureAwait(false);
                if (fromTemplate is not null)
                {
                    break;
                }
            }
        }

        if (request.Content is null)
        {
            return fromTemplate;
        }

        if (fromTemplate is null)
        {
            return request.Content;
        }

        var inline = request.Content;
        return new ChannelContent
        {
            Subject = inline.Subject ?? fromTemplate.Subject,
            Title = inline.Title ?? fromTemplate.Title,
            Text = inline.Text ?? fromTemplate.Text,
            Html = inline.Html ?? fromTemplate.Html,
            Data = inline.Data ?? fromTemplate.Data,
        };
    }

    private ChannelContent Render(ChannelContent content, TemplateData data) => new()
    {
        Subject = RenderPart(content.Subject, data, TemplateFormat.Text),
        Title = RenderPart(content.Title, data, TemplateFormat.Text),
        Text = RenderPart(content.Text, data, TemplateFormat.Text),
        Html = RenderPart(content.Html, data, TemplateFormat.Html),
        Data = RenderData(content.Data, data),
    };

    private string? RenderPart(string? template, TemplateData data, TemplateFormat format) =>
        template is null ? null : _renderer.Render(template, data, format);

    private Dictionary<string, string>? RenderData(IReadOnlyDictionary<string, string>? values, TemplateData data)
    {
        if (values is null)
        {
            return null;
        }

        var rendered = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            rendered[key] = _renderer.Render(value, data);
        }

        return rendered;
    }
}
