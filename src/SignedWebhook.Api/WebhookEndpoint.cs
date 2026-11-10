using System.Buffers;
using System.Globalization;
using Microsoft.Data.Sqlite;
using SignedWebhook.Protocol;

namespace SignedWebhook.Api;

internal static class WebhookEndpoint
{
    public const int MaxBodyBytes = 64 * 1024;

    public static async Task<IResult> HandleAsync(HttpRequest request, HttpResponse response,
        WebhookAuthenticator authenticator, PaymentStore store, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return Error("body_too_large", StatusCodes.Status413PayloadTooLarge);
        }

        var idText = SingleHeader(request, WebhookProtocol.IdHeader);
        var timestampText = SingleHeader(request, WebhookProtocol.TimestampHeader);
        if (idText is not { Length: 36 } || !Guid.TryParseExact(idText, "D", out var eventId)
            || timestampText is not { Length: >= 1 and <= 19 }
            || !timestampText.All(char.IsAsciiDigit)
            || !long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
        {
            return Error("invalid_headers", StatusCodes.Status400BadRequest);
        }

        var signatureText = SingleHeader(request, WebhookProtocol.SignatureHeader);
        var signature = new byte[WebhookProtocol.SignatureBytes];
        if (signatureText is not { Length: WebhookProtocol.SignatureBytes * 2 }
            || Convert.FromHexString(signatureText.AsSpan(), signature, out _, out _) != OperationStatus.Done)
        {
            return Unauthorized(response, "invalid_signature");
        }

        ReadOnlyMemory<byte>? body;
        try
        {
            body = await ReadBodyAsync(request.Body, cancellationToken);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Error("body_too_large", StatusCodes.Status413PayloadTooLarge);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status400BadRequest)
        {
            return Error("invalid_payload", StatusCodes.Status400BadRequest);
        }

        if (body is not { } rawBody)
        {
            return Error("body_too_large", StatusCodes.Status413PayloadTooLarge);
        }

        if (!authenticator.IsSignatureValid(timestampText, idText, rawBody.Span, signature))
        {
            return Unauthorized(response, "invalid_signature");
        }

        if (!authenticator.IsTimestampInWindow(timestamp))
        {
            return Unauthorized(response, "timestamp_outside_window");
        }

        var order = OrderPaidEvent.Parse(rawBody.Span);
        try
        {
            return store.Record(eventId, rawBody.Span, order) switch
            {
                DeliveryResult.Recorded => Results.Json(new { status = "recorded", eventId },
                    statusCode: StatusCodes.Status201Created),
                DeliveryResult.Duplicate => Results.Ok(new { status = "duplicate", eventId }),
                DeliveryResult.Conflict => Error("event_id_conflict", StatusCodes.Status409Conflict),
                DeliveryResult.InvalidPayload => Error("invalid_payload", StatusCodes.Status400BadRequest),
                DeliveryResult.PersistenceError => Error("persistence_error", StatusCodes.Status500InternalServerError),
                _ => throw new InvalidOperationException("Resultado de persistência desconhecido.")
            };
        }
        catch (SqliteException exception)
        {
            loggers.CreateLogger("SignedWebhook.Api.Webhooks").LogError(
                "Falha ao gravar a entrega no SQLite. Código SQLite {SqliteErrorCode}.", exception.SqliteErrorCode);
            return Error("persistence_error", StatusCodes.Status500InternalServerError);
        }
    }

    private static string? SingleHeader(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;

    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        // O byte adicional distingue um corpo no limite de um corpo excedente.
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                return buffer.AsMemory(0, total);
            }

            total += read;
        }

        return null;
    }

    private static IResult Unauthorized(HttpResponse response, string code)
    {
        response.Headers.WWWAuthenticate = "WebhookHmac realm=\"orders\"";
        return Error(code, StatusCodes.Status401Unauthorized);
    }

    private static IResult Error(string code, int status) =>
        Results.Json(new { error = code }, statusCode: status);
}
