namespace UniversalNotifier.NET;

/// <summary>Tells the caller why a send failed, which in turn decides whether sending again makes sense.</summary>
public enum NotifierErrorKind
{
    /// <summary>The message is invalid. Fix it and send again. Never retried.</summary>
    Validation,

    /// <summary>Credentials, region or provider setup is wrong. Never retried.</summary>
    Configuration,

    /// <summary>The provider refused the message for good (bad number, unregistered device). Never retried.</summary>
    Permanent,

    /// <summary>The provider did not accept the message and it is safe to repeat (rate limit, service unavailable).</summary>
    Transient,

    /// <summary>The outcome is not known (timeout, dropped connection). The provider may have processed it, so repeating can duplicate.</summary>
    Unknown,
}

/// <summary>A single failure reported by a notifier.</summary>
/// <param name="Kind">The failure category.</param>
/// <param name="Code">A stable, machine-readable code such as <c>email.recipients.required</c>.</param>
/// <param name="Message">A human-readable explanation. Never contains credentials or recipient addresses.</param>
public readonly record struct NotifierError(NotifierErrorKind Kind, string Code, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Code}: {Message}";
}
