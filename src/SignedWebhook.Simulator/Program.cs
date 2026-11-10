using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SignedWebhook.Protocol;

var concurrent = args.Length > 0 && args[0] == "--concurrent";
var idArguments = concurrent ? args[1..] : args;
var eventId = Guid.NewGuid();
if (idArguments.Length > 1 || (idArguments.Length == 1 && !Guid.TryParseExact(idArguments[0], "D", out eventId)))
{
    Console.Error.WriteLine("Uso: dotnet run --project src/SignedWebhook.Simulator -- [--concurrent] [UUID]");
    return 2;
}

var secret = Environment.GetEnvironmentVariable("WEBHOOK_SECRET");
if (string.IsNullOrWhiteSpace(secret))
{
    Console.Error.WriteLine("Defina WEBHOOK_SECRET com a mesma chave usada pela API.");
    return 2;
}

var address = Environment.GetEnvironmentVariable("WEBHOOK_URL") ?? "http://127.0.0.1:5080/webhooks/orders";
if (!Uri.TryCreate(address, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback
    || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
{
    Console.Error.WriteLine("Use uma URL HTTP ou HTTPS de loopback em WEBHOOK_URL.");
    return 2;
}

var id = eventId.ToString("D");
var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
var original = """{"type":"order.paid","orderId":"ord-1042","amountCents":25990,"currency":"BRL"}"""u8.ToArray();
var tampered = """{"type":"order.paid","orderId":"ord-1042","amountCents":39990,"currency":"BRL"}"""u8.ToArray();
var signature = Convert.ToHexStringLower(WebhookProtocol.Sign(Encoding.UTF8.GetBytes(secret), timestamp, id, original));

using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
{
    Timeout = TimeSpan.FromSeconds(15)
};
Console.WriteLine($"ID do evento {id}");

try
{
    if (concurrent)
    {
        var deliveries = await Task.WhenAll(
            SendAsync("Entrega concorrente A", original),
            SendAsync("Entrega concorrente B", original));
        return deliveries.Contains(HttpStatusCode.Created) && deliveries.Contains(HttpStatusCode.OK) ? 0 : 1;
    }

    var first = await SendAsync("Evento válido", original);
    var altered = await SendAsync("Corpo adulterado", tampered);
    var replay = await SendAsync("Reenvio válido", original);
    return first is HttpStatusCode.Created or HttpStatusCode.OK
           && altered == HttpStatusCode.Unauthorized
           && replay == HttpStatusCode.OK
        ? 0
        : 1;
}
catch (HttpRequestException)
{
    Console.Error.WriteLine("Falha na requisição. Confira se a API está disponível na URL local.");
    return 1;
}
catch (TaskCanceledException)
{
    Console.Error.WriteLine("O servidor excedeu o tempo de espera da requisição.");
    return 1;
}

async Task<HttpStatusCode> SendAsync(string label, byte[] body)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
    request.Content = new ByteArrayContent(body);
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    request.Headers.Add(WebhookProtocol.IdHeader, id);
    request.Headers.Add(WebhookProtocol.TimestampHeader, timestamp);
    request.Headers.Add(WebhookProtocol.SignatureHeader, signature);
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    Console.WriteLine($"{label} HTTP {(int)response.StatusCode}");
    return response.StatusCode;
}
