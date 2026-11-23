using System.Security.Cryptography;

namespace UniversalNotifier.NET.WebPush;

/// <summary>A VAPID key pair identifying your server to browser push services (RFC 8292).</summary>
/// <param name="PublicKey">The public key (base64url). Give it to the browser as the <c>applicationServerKey</c>.</param>
/// <param name="PrivateKey">The private key (base64url). Keep it secret.</param>
public sealed record VapidKeys(string PublicKey, string PrivateKey)
{
    /// <summary>Never prints the private key.</summary>
    public override string ToString() => $"VapidKeys {{ PublicKey = {PublicKey} }}";

    /// <summary>Generates a new P-256 key pair. Generate once, store the private key in a secret store and reuse it: changing it invalidates every existing subscription.</summary>
    public static VapidKeys Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);

        var publicKey = new byte[65];
        publicKey[0] = 0x04; // uncompressed point
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);

        return new VapidKeys(Base64Url.Encode(publicKey), Base64Url.Encode(parameters.D!));
    }
}

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
