using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace RelicCommerce;

public sealed class OrderStore : IDisposable
{
    private readonly string _connectionString;

    public OrderStore(CommerceOptions options)
    {
        if (!options.Enabled)
        {
            _connectionString = "Data Source=:memory:";
            using SqliteConnection disabledConnection = Open();
            Initialize(disabledConnection);
            return;
        }
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();

        string? directory = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using SqliteConnection connection = Open();
        Initialize(connection);
    }

    private static void Initialize(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS orders (
                session_id TEXT PRIMARY KEY,
                license_id TEXT NOT NULL UNIQUE,
                purchased_at INTEGER NOT NULL,
                application_major INTEGER NOT NULL,
                edition TEXT NOT NULL,
                email TEXT NOT NULL,
                name TEXT NOT NULL,
                status TEXT NOT NULL,
                payment_intent_id TEXT,
                delivery_token_hash TEXT,
                delivery_token_expires_at INTEGER,
                updates_until INTEGER NOT NULL DEFAULT 0,
                seat_limit INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS orders_payment_intent ON orders(payment_intent_id);
            CREATE TABLE IF NOT EXISTS blocked_payment_intents (
                payment_intent_id TEXT PRIMARY KEY,
                blocked_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS stripe_events (
                event_id TEXT PRIMARY KEY,
                event_type TEXT NOT NULL,
                first_seen_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS email_outbox (
                message_id INTEGER PRIMARY KEY AUTOINCREMENT,
                recipient TEXT NOT NULL,
                subject TEXT NOT NULL,
                body TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                sent_at INTEGER,
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error TEXT
            );
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "orders", "updates_until", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "orders", "seat_limit", "INTEGER NOT NULL DEFAULT 1");
        using SqliteCommand repair = connection.CreateCommand();
        repair.CommandText = "UPDATE orders SET updates_until = purchased_at + 31536000 WHERE updates_until <= 0";
        repair.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using SqliteCommand inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table})";
        using (SqliteDataReader reader = inspect.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal)) return;
            }
        }
        using SqliteCommand alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    public LicenseOrder? Get(string sessionId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectOrder + " WHERE session_id = $session_id";
        command.Parameters.AddWithValue("$session_id", sessionId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadOrder(reader) : null;
    }

    public LicenseOrder UpsertPaid(
        StripeSessionSnapshot session,
        DateTimeOffset purchasedAtUtc,
        int applicationMajor,
        string edition)
    {
        string licenseId = "RS-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        if (!string.IsNullOrWhiteSpace(session.PaymentIntentId))
        {
            using SqliteCommand blocked = connection.CreateCommand();
            blocked.Transaction = transaction;
            blocked.CommandText = "SELECT 1 FROM blocked_payment_intents WHERE payment_intent_id = $payment_intent_id LIMIT 1";
            blocked.Parameters.AddWithValue("$payment_intent_id", session.PaymentIntentId);
            if (blocked.ExecuteScalar() is not null)
            {
                transaction.Commit();
                throw new PaymentBlockedException();
            }
        }
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO orders
                    (session_id, license_id, purchased_at, application_major, edition, email,
                     name, status, payment_intent_id, updates_until, seat_limit)
                VALUES
                    ($session_id, $license_id, $purchased_at, $application_major, $edition, $email,
                     $name, 'paid', $payment_intent_id, $updates_until, 1)
                """;
            insert.Parameters.AddWithValue("$session_id", session.Id);
            insert.Parameters.AddWithValue("$license_id", licenseId);
            insert.Parameters.AddWithValue("$purchased_at", purchasedAtUtc.ToUnixTimeSeconds());
            insert.Parameters.AddWithValue("$application_major", applicationMajor);
            insert.Parameters.AddWithValue("$edition", edition);
            insert.Parameters.AddWithValue("$email", session.Email ?? "");
            insert.Parameters.AddWithValue("$name", session.Name ?? "");
            insert.Parameters.AddWithValue("$payment_intent_id", (object?)session.PaymentIntentId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$updates_until", purchasedAtUtc.AddYears(1).ToUnixTimeSeconds());
            insert.ExecuteNonQuery();
        }

        using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE orders
                SET status = CASE WHEN status = 'refunded' THEN status ELSE 'paid' END
                WHERE session_id = $session_id
                """;
            update.Parameters.AddWithValue("$session_id", session.Id);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
        return Get(session.Id)!;
    }

    public bool RecordEvent(string eventId, string eventType, DateTimeOffset nowUtc)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO stripe_events(event_id, event_type, first_seen_at)
            VALUES ($event_id, $event_type, $first_seen_at)
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$event_type", eventType);
        command.Parameters.AddWithValue("$first_seen_at", nowUtc.ToUnixTimeSeconds());
        return command.ExecuteNonQuery() == 1;
    }

    public long EnqueueEmail(string recipient, string subject, string body, DateTimeOffset nowUtc)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO email_outbox(recipient, subject, body, created_at)
            VALUES ($recipient, $subject, $body, $created_at);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$recipient", recipient);
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$created_at", nowUtc.ToUnixTimeSeconds());
        return (long)command.ExecuteScalar()!;
    }

    public void MarkRefundedByPaymentIntent(string paymentIntentId)
    {
        if (string.IsNullOrWhiteSpace(paymentIntentId)) return;
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using (SqliteCommand block = connection.CreateCommand())
        {
            block.Transaction = transaction;
            block.CommandText = "INSERT OR IGNORE INTO blocked_payment_intents(payment_intent_id, blocked_at) VALUES ($payment_intent_id, $blocked_at)";
            block.Parameters.AddWithValue("$payment_intent_id", paymentIntentId);
            block.Parameters.AddWithValue("$blocked_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            block.ExecuteNonQuery();
        }
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE orders SET status = 'refunded' WHERE payment_intent_id = $payment_intent_id";
        command.Parameters.AddWithValue("$payment_intent_id", paymentIntentId);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public string IssueDeliveryToken(string sessionId, DateTimeOffset expiresUtc)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        string token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE orders
            SET delivery_token_hash = $hash, delivery_token_expires_at = $expires
            WHERE session_id = $session_id AND status = 'paid'
            """;
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$expires", expiresUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$session_id", sessionId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The paid order is not available for delivery.");
        return token;
    }

    public LicenseOrder? GetByDeliveryToken(string token, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return null;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectOrder + " WHERE delivery_token_hash = $hash AND status = 'paid'";
        command.Parameters.AddWithValue("$hash", hash);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        long? expires = reader.IsDBNull(10) ? null : reader.GetInt64(10);
        return expires is > 0 && expires > nowUtc.ToUnixTimeSeconds() ? ReadOrder(reader) : null;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private const string SelectOrder = """
        SELECT session_id, license_id, purchased_at, application_major, edition, email, name,
               status, payment_intent_id, delivery_token_hash, delivery_token_expires_at,
               updates_until, seat_limit
        FROM orders
        """;

    private static LicenseOrder ReadOrder(SqliteDataReader reader)
    {
        DateTimeOffset? expires = reader.IsDBNull(10)
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(10));
        DateTimeOffset purchased = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2));
        DateTimeOffset updatesUntil = reader.IsDBNull(11) || reader.GetInt64(11) <= 0
            ? purchased.AddYears(1)
            : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(11));
        return new LicenseOrder(
            reader.GetString(0),
            reader.GetString(1),
            purchased,
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            expires)
        {
            UpdatesUntilUtc = updatesUntil,
            SeatLimit = reader.IsDBNull(12) ? 1 : reader.GetInt32(12),
        };
    }

    public void Dispose() { }
}

internal sealed class PaymentBlockedException : InvalidOperationException
{
    public PaymentBlockedException()
        : base("The payment intent is blocked from fulfillment.") { }
}
