namespace UniversalNotifier.NET;

/// <summary>Collects validation errors without allocating when a message is valid.</summary>
internal struct ValidationErrors
{
    private List<NotifierError>? _errors;

    public void Add(string code, string message) =>
        (_errors ??= []).Add(new NotifierError(NotifierErrorKind.Validation, code, message));

    public readonly IReadOnlyList<NotifierError> ToList() => _errors ?? (IReadOnlyList<NotifierError>)[];

    public static bool HasLineBreak(string? value) =>
        value is not null && value.AsSpan().IndexOfAny('\r', '\n') >= 0;

    public static bool HasControlCharacter(string? value)
    {
        if (value is null)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }
}
