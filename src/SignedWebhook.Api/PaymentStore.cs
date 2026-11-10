using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace SignedWebhook.Api;

internal enum DeliveryResult
{
    Recorded,
    Duplicate,
    Conflict,
    InvalidPayload,
    PersistenceError
}

internal sealed class PaymentStore
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public PaymentStore(IConfiguration configuration)
    {
        _databasePath = Path.GetFullPath(configuration["WEBHOOK_DB_PATH"] ?? "data/webhooks.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
    }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var schema = connection.CreateCommand();
        schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS received_events (
                event_id TEXT NOT NULL PRIMARY KEY,
                body_sha256 BLOB NOT NULL CHECK (length(body_sha256) = 32)
            );

            CREATE TABLE IF NOT EXISTS payments (
                event_id TEXT NOT NULL PRIMARY KEY REFERENCES received_events (event_id),
                order_id TEXT NOT NULL,
                amount_cents INTEGER NOT NULL CHECK (amount_cents > 0),
                currency TEXT NOT NULL
            );
            """;
        schema.ExecuteNonQuery();
        transaction.Commit();
    }

    public DeliveryResult Record(Guid eventId, ReadOnlySpan<byte> body, OrderPaidEvent? order)
    {
        var id = eventId.ToString("D");
        var fingerprint = SHA256.HashData(body);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // BEGIN IMMEDIATE serializa a consulta e a gravação entre conexões.
        using var transaction = connection.BeginTransaction(deferred: false);
        using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT body_sha256 FROM received_events WHERE event_id = $id;";
        lookup.Parameters.AddWithValue("$id", id);
        if (lookup.ExecuteScalar() is byte[] storedFingerprint)
        {
            return CryptographicOperations.FixedTimeEquals(fingerprint, storedFingerprint)
                ? DeliveryResult.Duplicate
                : DeliveryResult.Conflict;
        }

        if (order is null)
        {
            return DeliveryResult.InvalidPayload;
        }

        using var register = connection.CreateCommand();
        register.Transaction = transaction;
        register.CommandText = """
            INSERT INTO received_events (event_id, body_sha256) VALUES ($id, $fingerprint);
            """;
        register.Parameters.AddWithValue("$id", id);
        register.Parameters.Add("$fingerprint", SqliteType.Blob).Value = fingerprint;
        using var payment = connection.CreateCommand();
        payment.Transaction = transaction;
        payment.CommandText = """
            INSERT INTO payments (event_id, order_id, amount_cents, currency)
            VALUES ($id, $orderId, $amountCents, $currency);
            """;
        payment.Parameters.AddWithValue("$id", id);
        payment.Parameters.AddWithValue("$orderId", order.OrderId);
        payment.Parameters.AddWithValue("$amountCents", order.AmountCents);
        payment.Parameters.AddWithValue("$currency", order.Currency);
        if (register.ExecuteNonQuery() != 1 || payment.ExecuteNonQuery() != 1)
        {
            return DeliveryResult.PersistenceError;
        }

        transaction.Commit();
        return DeliveryResult.Recorded;
    }
}
