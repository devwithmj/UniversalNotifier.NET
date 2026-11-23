namespace UniversalNotifier.NET;

/// <summary>Base type of everything a notifier can send. A message carries its own recipients and content.</summary>
public abstract record NotifierMessage
{
    /// <summary>An optional caller-supplied correlation id, copied into logs and results.</summary>
    public string? Id { get; init; }

    /// <summary>Optional free-form key/value data for tracing. Providers may forward it where they support tags or custom data.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Checks the message and returns its problems. Returns an empty list when the message is valid.</summary>
    public abstract IReadOnlyList<NotifierError> Validate();

    /// <summary>Prints only the type and id so recipients and content never leak into logs.</summary>
    public sealed override string ToString() => $"{GetType().Name} {{ Id = {Id ?? "-"} }}";
}
