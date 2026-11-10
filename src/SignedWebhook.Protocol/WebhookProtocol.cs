using System.Security.Cryptography;
using System.Text;

namespace SignedWebhook.Protocol;

public static class WebhookProtocol
{
    public const string IdHeader = "X-Webhook-Id";
    public const string TimestampHeader = "X-Webhook-Timestamp";
    public const string SignatureHeader = "X-Webhook-Signature";
    public const int SignatureBytes = 32;

    public static byte[] Sign(
        ReadOnlySpan<byte> secret, string timestamp, string id, ReadOnlySpan<byte> body)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, secret);
        hmac.AppendData(Encoding.UTF8.GetBytes($"v1\n{timestamp}\n{id}\n"));
        hmac.AppendData(body);
        return hmac.GetHashAndReset();
    }
}
