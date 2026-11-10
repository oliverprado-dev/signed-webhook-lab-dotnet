using System.Text.Json;
using System.Text.Json.Serialization;

namespace SignedWebhook.Api;

internal sealed record OrderPaidEvent(string Type, string OrderId, long AmountCents, string Currency)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static OrderPaidEvent? Parse(ReadOnlySpan<byte> body)
    {
        try
        {
            var order = JsonSerializer.Deserialize<OrderPaidEvent>(body, JsonOptions);
            if (order is not { Type: "order.paid", AmountCents: > 0, OrderId.Length: >= 1 and <= 64, Currency.Length: 3 }
                || !order.OrderId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                || !order.Currency.All(char.IsAsciiLetterUpper))
            {
                return null;
            }

            return order;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
