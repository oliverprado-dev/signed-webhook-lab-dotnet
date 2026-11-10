using System.Text;
using SignedWebhook.Protocol;
using Xunit;

namespace SignedWebhook.IntegrationTests;

public sealed class SignatureTests
{
    [Fact]
    public void Matches_the_fixed_hmac_vector()
    {
        var publicKey = Encoding.UTF8.GetBytes("vetor-publico-hmac-v1");
        const string timestamp = "1700000000";
        const string id = "123e4567-e89b-42d3-a456-426614174000";
        var body = """{"type":"order.paid","orderId":"ord-1042","amountCents":25990,"currency":"BRL"}"""u8;
        const string expected = "17920e058f6140d1f2be8aa8aa8abcd333285efd60533a177a549234bb748701";

        var signature = WebhookProtocol.Sign(publicKey, timestamp, id, body);

        Assert.Equal(expected, Convert.ToHexStringLower(signature));
    }
}
