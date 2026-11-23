using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UniversalNotifier.NET.Email.Ses;

/// <summary>AWS Signature Version 4 for a request with a known body (all SES needs). Kept small instead of pulling in the AWS SDK.</summary>
internal static class AwsSigV4
{
    public readonly record struct Signed(string AmzDate, string Authorization);

    public static Signed Sign(
        string method,
        string host,
        string path,
        string canonicalQuery,
        ReadOnlySpan<byte> body,
        string contentType,
        string region,
        string service,
        string accessKeyId,
        string secretAccessKey,
        string? sessionToken,
        DateTimeOffset now)
    {
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = amzDate[..8];

        // Header names sorted ordinally, as the canonical form requires.
        var headers = new StringBuilder()
            .Append("content-type:").Append(contentType).Append('\n')
            .Append("host:").Append(host).Append('\n')
            .Append("x-amz-date:").Append(amzDate).Append('\n');
        var signedHeaders = "content-type;host;x-amz-date";
        if (!string.IsNullOrEmpty(sessionToken))
        {
            headers.Append("x-amz-security-token:").Append(sessionToken).Append('\n');
            signedHeaders += ";x-amz-security-token";
        }

        var canonicalRequest = $"{method}\n{path}\n{canonicalQuery}\n{headers}\n{signedHeaders}\n{Hex(SHA256.HashData(body))}";
        var scope = $"{date}/{region}/{service}/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), date);
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");
        var signature = Hex(Hmac(key, stringToSign));

        return new Signed(amzDate, $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
