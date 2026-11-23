namespace UniversalNotifier.NET;

/// <summary>A phone number in E.164 format (<c>+14165551234</c>). A plain string converts implicitly.</summary>
/// <param name="Value">The number including the leading <c>+</c> and country code.</param>
public readonly record struct PhoneNumber(string Value)
{
    /// <summary>Converts a plain string.</summary>
    public static implicit operator PhoneNumber(string value) => new(value);

    /// <summary><see langword="true"/> for <c>+</c> followed by 7 to 15 digits, the first one not zero.</summary>
    public bool IsValid
    {
        get
        {
            var v = Value.AsSpan();
            if (v.Length is < 8 or > 16 || v[0] != '+' || v[1] is < '1' or > '9')
            {
                return false;
            }

            foreach (var c in v[2..])
            {
                if (c is < '0' or > '9')
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
