using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace UniversalNotifier.NET;

/// <summary>The shared services every notifier needs: logging, the clock and retry settings.</summary>
public sealed class NotifierContext
{
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Creates a context. Every argument is optional; defaults are no logging, the system clock and no retries.</summary>
    public NotifierContext(NotifierOptions? options = null, ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null)
    {
        Retry = (options ?? new NotifierOptions()).Retry;
        TimeProvider = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>The retry settings.</summary>
    public RetryOptions Retry { get; }

    /// <summary>The clock used for retry delays. Replace it in tests.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>Creates a logger for the given notifier type.</summary>
    public ILogger CreateLogger(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _loggerFactory.CreateLogger(type.FullName ?? type.Name);
    }
}
