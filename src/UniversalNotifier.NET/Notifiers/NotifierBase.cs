using Microsoft.Extensions.Logging;

namespace UniversalNotifier.NET;

/// <summary>
/// The base class for providers. It owns the rules every provider shares (validation, one error model, cancellation,
/// logging, optional retries) so a provider only implements the call to its service.
/// </summary>
/// <typeparam name="TMessage">The message type this provider sends.</typeparam>
public abstract class NotifierBase<TMessage> : INotifier<TMessage> where TMessage : NotifierMessage
{
    private readonly NotifierContext _context;
    private readonly ILogger _logger;
    private readonly RateGate? _rateGate;

    /// <summary>Creates the notifier.</summary>
    protected NotifierBase(NotifierContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _logger = context.CreateLogger(GetType());
        _rateGate = context.RateLimit.Enabled ? new RateGate(context.RateLimit, context.TimeProvider) : null;
    }

    /// <summary>The name used in logs. Defaults to the class name.</summary>
    protected virtual string ProviderName => GetType().Name;

    /// <inheritdoc />
    public async Task<NotifierResponse> SendAsync(TMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var messageType = typeof(TMessage).Name;
        var id = message.Id ?? "-";

        var errors = message.Validate();
        if (errors.Count > 0)
        {
            NotifierLog.Invalid(_logger, ProviderName, messageType, id, JoinCodes(errors));
            return NotifierResponse.Failed(errors);
        }

        var retry = _context.Retry;
        for (var attempt = 1; ; attempt++)
        {
            if (_rateGate is not null)
            {
                await _rateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            NotifierLog.Sending(_logger, ProviderName, messageType, id, attempt);
            var response = await TrySendAsync(message, cancellationToken).ConfigureAwait(false);

            if (response.Success)
            {
                NotifierLog.Completed(_logger, ProviderName, response.Status, messageType, id, response.ProviderMessageId);
                return response;
            }

            if (attempt >= retry.MaxAttempts || !CanRetry(response, retry))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    NotifierLog.Failed(_logger, ProviderName, response.Status, messageType, id, attempt, JoinCodes(response.Errors));
                }

                return response;
            }

            var delay = GetDelay(retry, attempt, response.RetryAfter);
            NotifierLog.Retrying(_logger, ProviderName, messageType, id, delay, attempt);
            await Task.Delay(delay, _context.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Makes the provider call. Return a failed response for provider rejections. Exceptions that escape are mapped by
    /// <see cref="Classify"/>, except cancellation, which propagates.
    /// </summary>
    protected abstract Task<NotifierResponse> SendCoreAsync(TMessage message, CancellationToken cancellationToken);

    /// <summary>Maps an unexpected exception to an error. Override to recognise a provider's own exception types.</summary>
    protected virtual NotifierError Classify(Exception exception) => exception switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError }
            => new NotifierError(NotifierErrorKind.Transient, "network.unreachable", "The provider could not be reached."),
        HttpRequestException
            => new NotifierError(NotifierErrorKind.Unknown, "network.error", "The request to the provider failed."),
        OperationCanceledException or TimeoutException
            => new NotifierError(NotifierErrorKind.Unknown, "network.timeout", "The request to the provider timed out."),
        _ => new NotifierError(NotifierErrorKind.Unknown, "provider.error", $"{exception.GetType().Name}: {exception.Message}"),
    };

    private async Task<NotifierResponse> TrySendAsync(TMessage message, CancellationToken cancellationToken)
    {
        try
        {
            return await SendCoreAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return NotifierResponse.Failed(Classify(exception));
        }
    }

    // Only a total failure is retried: repeating after a partial success would message the accepted recipients twice.
    private static bool CanRetry(NotifierResponse response, RetryOptions retry)
    {
        if (response.Status != DeliveryStatus.Failed || response.Errors.Count == 0)
        {
            return false;
        }

        foreach (var error in response.Errors)
        {
            var retryable = error.Kind == NotifierErrorKind.Transient
                || (error.Kind == NotifierErrorKind.Unknown && retry.RetryUnknownFailures);
            if (!retryable)
            {
                return false;
            }
        }

        return true;
    }

    private static TimeSpan GetDelay(RetryOptions retry, int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } hint)
        {
            return hint < retry.MaxDelay ? hint : retry.MaxDelay;
        }

        // Equal jitter: half the exponential delay is fixed, half is random, so clients do not retry in lockstep.
        var ceiling = Math.Min(retry.MaxDelay.TotalMilliseconds, retry.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds((ceiling / 2) + (Random.Shared.NextDouble() * ceiling / 2));
    }

    private static string JoinCodes(IReadOnlyList<NotifierError> errors) =>
        errors.Count == 1 ? errors[0].Code : string.Join(',', errors.Select(e => e.Code));
}
