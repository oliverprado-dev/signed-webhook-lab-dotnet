using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SignedWebhook.Protocol;
using Xunit;

namespace SignedWebhook.IntegrationTests;

public sealed class WebhookIntegrationTests : IDisposable
{
    private const int BodyLimit = 65_536;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly byte[] Original =
        """{"type":"order.paid","orderId":"ord-1042","amountCents":25990,"currency":"BRL"}"""u8.ToArray();
    private readonly string _eventId = Guid.NewGuid().ToString("D");
    private readonly string _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("signed-webhook-");
    private LabFactory? _factory;
    private HttpClient? _client;
    private string DatabasePath => Path.Combine(_directory.FullName, "webhooks.db");

    [Fact]
    public async Task Valid_event_records_exactly_one_payment()
    {
        using var response = await SendAsync(Original);

        await AssertResponseAsync(response, HttpStatusCode.Created, "recorded");
        AssertOnePayment(Original);
    }

    [Theory]
    [InlineData("{\"type\":\"order.paid\",\"orderId\":\"ord-1042\",\"amountCents\":39990,\"currency\":\"BRL\"}")]
    [InlineData("{invalid-json")]
    public async Task Tampered_body_is_rejected_before_parsing_or_writing(string altered)
    {
        using var response = await SendAsync(Encoding.UTF8.GetBytes(altered), signedBody: Original);

        await AssertResponseAsync(response, HttpStatusCode.Unauthorized, "invalid_signature");
        AssertDatabaseIsEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("malformed")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task Invalid_signature_is_rejected(string? signature)
    {
        using var response = await SendAsync(Original, edit: request =>
        {
            request.Headers.Remove(WebhookProtocol.SignatureHeader);
            if (signature is not null)
            {
                request.Headers.Add(WebhookProtocol.SignatureHeader, signature);
            }
        });

        await AssertResponseAsync(response, HttpStatusCode.Unauthorized, "invalid_signature");
        AssertDatabaseIsEmpty();
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public async Task Timestamp_outside_the_window_is_rejected(int secondsFromNow)
    {
        using var response = await SendAsync(Original, secondsFromNow: secondsFromNow);

        await AssertResponseAsync(response, HttpStatusCode.Unauthorized, "timestamp_outside_window");
        AssertDatabaseIsEmpty();
    }

    [Theory]
    [InlineData(-300)]
    [InlineData(300)]
    public async Task Timestamp_window_includes_both_boundaries(int secondsFromNow)
    {
        using var response = await SendAsync(Original, secondsFromNow: secondsFromNow);

        await AssertResponseAsync(response, HttpStatusCode.Created, "recorded");
        AssertOnePayment(Original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_body_is_rejected_with_or_without_content_length(bool chunked)
    {
        using var response = await SendAsync(PaddedBody(BodyLimit + 1), chunked: chunked);

        await AssertResponseAsync(response, HttpStatusCode.RequestEntityTooLarge, "body_too_large");
        AssertDatabaseIsEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Body_at_the_exact_limit_is_accepted_with_or_without_content_length(bool chunked)
    {
        var body = PaddedBody(BodyLimit);
        using var response = await SendAsync(body, chunked: chunked);

        await AssertResponseAsync(response, HttpStatusCode.Created, "recorded");
        AssertOnePayment(body);
    }

    [Theory]
    [InlineData("{invalid-json")]
    [InlineData("{\"type\":\"order.paid\",\"orderId\":\"ord-1042\",\"currency\":\"BRL\"}")]
    [InlineData("{\"type\":\"order.paid\",\"orderId\":\"ord-1042\",\"amountCents\":-1,\"currency\":\"BRL\"}")]
    [InlineData("{\"type\":\"order.paid\",\"orderId\":\"ord-1042\",\"amountCents\":25990,\"amountCents\":25991,\"currency\":\"BRL\"}")]
    public async Task Invalid_signed_payload_is_rejected(string body)
    {
        using var response = await SendAsync(Encoding.UTF8.GetBytes(body));

        await AssertResponseAsync(response, HttpStatusCode.BadRequest, "invalid_payload");
        AssertDatabaseIsEmpty();
    }

    [Fact]
    public async Task Invalid_utf8_in_a_signed_body_is_rejected()
    {
        var malformed = (byte[])Original.Clone();
        malformed[Array.IndexOf(malformed, (byte)'B')] = 0xff;
        using var response = await SendAsync(malformed);

        await AssertResponseAsync(response, HttpStatusCode.BadRequest, "invalid_payload");
        AssertDatabaseIsEmpty();
    }

    [Theory]
    [InlineData(WebhookProtocol.IdHeader, "no-uuid")]
    [InlineData(WebhookProtocol.TimestampHeader, "9223372036854775808")]
    public async Task Invalid_identification_headers_are_rejected(string header, string value)
    {
        using var response = await SendAsync(Original, edit: request =>
        {
            request.Headers.Remove(header);
            request.Headers.Add(header, value);
        });

        await AssertResponseAsync(response, HttpStatusCode.BadRequest, "invalid_headers");
        AssertDatabaseIsEmpty();
    }

    [Fact]
    public async Task Sequential_replays_preserve_one_payment()
    {
        using var first = await SendAsync(Original);
        using var second = await SendAsync(Original);
        using var third = await SendAsync(Original, id: _eventId.ToUpperInvariant(), secondsFromNow: 1);

        await AssertResponseAsync(first, HttpStatusCode.Created, "recorded");
        await AssertResponseAsync(second, HttpStatusCode.OK, "duplicate");
        await AssertResponseAsync(third, HttpStatusCode.OK, "duplicate");
        AssertOnePayment(Original);
    }

    [Fact]
    public async Task Concurrent_replays_preserve_one_payment()
    {
        StartApi(new RequestGate());
        var responses = await Task.WhenAll(SendAsync(Original), SendAsync(Original));
        using var first = responses[0];
        using var second = responses[1];
        var recorded = first.StatusCode == HttpStatusCode.Created ? first : second;
        var duplicate = ReferenceEquals(first, recorded) ? second : first;

        await AssertResponseAsync(recorded, HttpStatusCode.Created, "recorded");
        await AssertResponseAsync(duplicate, HttpStatusCode.OK, "duplicate");
        AssertOnePayment(Original);
    }

    [Theory]
    [InlineData("{\"type\":\"order.paid\",\"orderId\":\"ord-1042\",\"amountCents\":39990,\"currency\":\"BRL\"}")]
    [InlineData("{\"type\":\"order.paid\",\"orderId\":\"ord-1042\",\"amountCents\":25990,\"currency\":\"BRL\"} ")]
    [InlineData("{invalid-json")]
    public async Task Reusing_an_id_with_a_different_signed_body_conflicts(string otherBody)
    {
        using var first = await SendAsync(Original);
        using var conflict = await SendAsync(Encoding.UTF8.GetBytes(otherBody));

        await AssertResponseAsync(first, HttpStatusCode.Created, "recorded");
        await AssertResponseAsync(conflict, HttpStatusCode.Conflict, "event_id_conflict");
        AssertOnePayment(Original);
    }

    [Fact]
    public async Task Restart_preserves_idempotency()
    {
        using (var first = await SendAsync(Original))
        {
            await AssertResponseAsync(first, HttpStatusCode.Created, "recorded");
        }

        StopApi();
        using var second = await SendAsync(Original, secondsFromNow: 1);

        await AssertResponseAsync(second, HttpStatusCode.OK, "duplicate");
        AssertOnePayment(Original);
    }

    [Theory]
    [InlineData("RAISE(ABORT, 'test failure')")]
    [InlineData("RAISE(IGNORE)")]
    public async Task Failed_payment_rolls_back_the_event_and_allows_retry(string triggerAction)
    {
        StartApi();
        ExecuteSql($"""
            CREATE TRIGGER reject_payment BEFORE INSERT ON payments
            BEGIN
                SELECT {triggerAction};
            END;
            """);

        using var failed = await SendAsync(Original);
        await AssertResponseAsync(failed, HttpStatusCode.InternalServerError, "persistence_error");
        AssertDatabaseIsEmpty();

        ExecuteSql("DROP TRIGGER reject_payment;");
        using var retry = await SendAsync(Original);
        await AssertResponseAsync(retry, HttpStatusCode.Created, "recorded");
        AssertOnePayment(Original);
    }

    private HttpClient StartApi(RequestGate? gate = null)
    {
        _factory = new LabFactory(DatabasePath, _secret, gate);
        _factory.UseKestrel(0);
        _factory.StartServer();
        _client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = _factory.ClientOptions.BaseAddress,
            Timeout = TimeSpan.FromSeconds(15)
        };
        return _client;
    }

    private void StopApi()
    {
        _client?.Dispose();
        _factory?.Dispose();
        _client = null;
        _factory = null;
    }

    private async Task<HttpResponseMessage> SendAsync(byte[] body, string? id = null, int secondsFromNow = 0,
        byte[]? signedBody = null, bool chunked = false, Action<HttpRequestMessage>? edit = null)
    {
        var client = _client ?? StartApi();
        var signedId = id ?? _eventId;
        var timestamp = Now.AddSeconds(secondsFromNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = WebhookProtocol.Sign(Encoding.UTF8.GetBytes(_secret), timestamp, signedId, signedBody ?? body);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/orders");
        request.Content = chunked ? new ChunkedContent(body) : new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (chunked)
        {
            request.Headers.TransferEncodingChunked = true;
        }

        request.Headers.Add(WebhookProtocol.IdHeader, signedId);
        request.Headers.Add(WebhookProtocol.TimestampHeader, timestamp);
        request.Headers.Add(WebhookProtocol.SignatureHeader, Convert.ToHexStringLower(signature));
        edit?.Invoke(request);
        return await client.SendAsync(request);
    }

    private async Task AssertResponseAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ResponseEnvelope>();
        Assert.NotNull(envelope);
        if (status is HttpStatusCode.Created or HttpStatusCode.OK)
        {
            Assert.Equal(code, envelope.Status);
            Assert.Equal(_eventId, envelope.EventId);
        }
        else
        {
            Assert.Equal(code, envelope.Error);
        }
    }

    private void AssertDatabaseIsEmpty()
    {
        using var connection = OpenDatabase();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT (SELECT count(*) FROM received_events), (SELECT count(*) FROM payments);";
        using var rows = query.ExecuteReader();
        Assert.True(rows.Read());
        Assert.Equal(0, rows.GetInt64(0));
        Assert.Equal(0, rows.GetInt64(1));
    }

    private void AssertOnePayment(byte[] signedBody)
    {
        using var connection = OpenDatabase();
        using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT event.event_id, event.body_sha256, payment.order_id, payment.amount_cents, payment.currency,
                   (SELECT count(*) FROM received_events), (SELECT count(*) FROM payments)
            FROM received_events AS event JOIN payments AS payment USING (event_id);
            """;
        using var rows = query.ExecuteReader();
        Assert.True(rows.Read());
        Assert.Equal(_eventId, rows.GetString(0));
        Assert.Equal(SHA256.HashData(signedBody), (byte[])rows.GetValue(1));
        Assert.Equal("ord-1042", rows.GetString(2));
        Assert.Equal(25990, rows.GetInt64(3));
        Assert.Equal("BRL", rows.GetString(4));
        Assert.Equal(1, rows.GetInt64(5));
        Assert.Equal(1, rows.GetInt64(6));
        Assert.False(rows.Read());
    }

    private SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private void ExecuteSql(string sql)
    {
        using var connection = OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static byte[] PaddedBody(int size)
    {
        var body = new byte[size];
        Original.CopyTo(body, 0);
        body.AsSpan(Original.Length).Fill((byte)' ');
        return body;
    }

    public void Dispose()
    {
        StopApi();
        _directory.Delete(recursive: true);
    }

    private sealed record ResponseEnvelope(string? Status, string? Error, string? EventId);

    private sealed class LabFactory(string databasePath, string secret, RequestGate? gate)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["WEBHOOK_SECRET"] = secret,
                    ["WEBHOOK_DB_PATH"] = databasePath
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider());
                if (gate is not null)
                {
                    services.AddSingleton<IStartupFilter>(gate);
                }
            });
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ChunkedContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body.AsMemory()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class RequestGate : IStartupFilter
    {
        private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continueRequest) =>
            {
                if (Interlocked.Increment(ref _arrivals) == 2)
                {
                    _bothArrived.TrySetResult();
                }

                await _bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), context.RequestAborted);
                await continueRequest(context);
            });
            next(app);
        };
    }
}
