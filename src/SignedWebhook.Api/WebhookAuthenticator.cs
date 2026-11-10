using System.Security.Cryptography;
using System.Text;
using SignedWebhook.Protocol;

namespace SignedWebhook.Api;

internal sealed class WebhookAuthenticator
{
    private const long WindowSeconds = 300;
    private readonly byte[] _secret;
    private readonly TimeProvider _clock;

    public WebhookAuthenticator(IConfiguration configuration, TimeProvider clock)
    {
        var secret = configuration["WEBHOOK_SECRET"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("Defina WEBHOOK_SECRET antes de iniciar a API.");
        }

        _secret = Encoding.UTF8.GetBytes(secret);
        _clock = clock;
    }

    public bool IsSignatureValid(string timestamp, string id, ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> signature)
    {
        var expected = WebhookProtocol.Sign(_secret, timestamp, id, body);
        return CryptographicOperations.FixedTimeEquals(expected, signature);
    }

    public bool IsTimestampInWindow(long timestamp)
    {
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        return timestamp >= now - WindowSeconds && timestamp <= now + WindowSeconds;
    }
}
