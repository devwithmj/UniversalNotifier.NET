namespace UniversalNotifier.NET;

/// <summary>An email address with an optional display name. A plain string converts implicitly.</summary>
/// <param name="Address">The address, for example <c>user@example.com</c>.</param>
/// <param name="Name">The optional display name.</param>
public readonly record struct EmailAddress(string Address, string? Name = null)
{
    /// <summary>Converts a plain address string.</summary>
    public static implicit operator EmailAddress(string address) => new(address);

    /// <summary>
    /// <see langword="true"/> for a plain <c>local@domain.tld</c> address without spaces, control characters or
    /// header-breaking characters. Quoted local parts and address literals are intentionally rejected.
    /// </summary>
    public bool IsValid => IsValidAddress(Address) && !ValidationErrors.HasControlCharacter(Name);

    /// <summary>Formats as <c>address</c> or a quoted-name mailbox, so commas and quotes in the name cannot split or inject recipients.</summary>
    public override string ToString()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return Address ?? string.Empty;
        }

        var quoted = Name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{quoted}\" <{Address}>";
    }

    private static bool IsValidAddress(ReadOnlySpan<char> address)
    {
        if (address.IsEmpty || address.Length > 254)
        {
            return false;
        }

        var at = address.IndexOf('@');
        if (at <= 0 || at != address.LastIndexOf('@') || at == address.Length - 1)
        {
            return false;
        }

        foreach (var c in address)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or '(' or ')' or ',' or ';' or ':' or '"' or '\\' or '[' or ']')
            {
                return false;
            }
        }

        var local = address[..at];
        var domain = address[(at + 1)..];

        return local.Length <= 64
            && local[0] != '.' && local[^1] != '.' && !local.Contains("..", StringComparison.Ordinal)
            && domain.Length <= 253
            && domain.IndexOf('.') > 0
            && domain[0] != '.' && domain[0] != '-' && domain[^1] != '.'
            && !domain.Contains("..", StringComparison.Ordinal);
    }
}
