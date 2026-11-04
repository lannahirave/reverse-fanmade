using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ReVerse.Capture.Protocol;


public sealed class RelayAccounts
{
    public const int MaxUsernameLength = 64;
    private readonly string connectionString;
    private readonly object gate = new();

    public RelayAccounts(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var path = Path.GetFullPath(configuration["Relay:DatabasePath"] ?? "data/accounts.db", environment.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
        using var connection = Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS accounts (
                    id TEXT PRIMARY KEY, username TEXT NOT NULL,
                    username_key TEXT NOT NULL UNIQUE, created_at INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS relay_sessions (
                    token_hash BLOB PRIMARY KEY, account_id TEXT NOT NULL REFERENCES accounts(id),
                    expires_at INTEGER NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }
        UpgradeAccounts(connection);
    }

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public static string? UsernameKey(string username)
    {
        try { return username.Normalize(NormalizationForm.FormKC).ToUpperInvariant(); }
        catch (ArgumentException) { return null; }
    }

    public LoginResult Login(string? username)
    {
        username = username?.Trim();
        if (string.IsNullOrEmpty(username) || username.Length > MaxUsernameLength || username.Any(char.IsControl))
            throw new BadHttpRequestException("A username of 1–64 characters is required.", 400);
        var key = UsernameKey(username)
            ?? throw new BadHttpRequestException("The username contains invalid characters.", 400);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expires = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            SqliteCommand Command(string sql)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                return command;
            }
            string accountId, name;
            var created = false;
            using (var find = Command("SELECT id, username FROM accounts WHERE username_key = $key"))
            {
                find.Parameters.AddWithValue("$key", key);
                using var reader = find.ExecuteReader();
                if (reader.Read()) (accountId, name) = (reader.GetString(0), reader.GetString(1));
                else (accountId, name, created) = (Guid.NewGuid().ToString("N"), username, true);
            }
            if (created)
            {
                using var insert = Command("INSERT INTO accounts (id, username, username_key, created_at) VALUES ($id, $name, $key, $now)");
                insert.Parameters.AddWithValue("$id", accountId);
                insert.Parameters.AddWithValue("$name", name);
                insert.Parameters.AddWithValue("$key", key);
                insert.Parameters.AddWithValue("$now", now);
                insert.ExecuteNonQuery();
            }
            using var session = Command("""
                DELETE FROM relay_sessions WHERE expires_at <= $now;
                INSERT INTO relay_sessions VALUES ($token, $id, $expires);
                """);
            session.Parameters.AddWithValue("$now", now);
            session.Parameters.AddWithValue("$token", HashToken(token));
            session.Parameters.AddWithValue("$id", accountId);
            session.Parameters.AddWithValue("$expires", expires);
            session.ExecuteNonQuery();
            transaction.Commit();
            return new LoginResult(accountId, name, token, expires, created);
        }
    }

    public SteamIdentity Resolve(string? token)
    {
        if (token is null || token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new BadHttpRequestException("Start the relay to sign in.", 401);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.username FROM relay_sessions s JOIN accounts a ON a.id = s.account_id
            WHERE s.token_hash = $token AND s.expires_at > $now
            """;
        command.Parameters.AddWithValue("$token", HashToken(token));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new BadHttpRequestException("Relay session expired or invalid; stop and start the relay.", 401);



        return new SteamIdentity(reader.GetString(0), reader.GetString(1));
    }

    private static void UpgradeAccounts(SqliteConnection connection)
    {
        SqliteCommand Command(string sql, SqliteTransaction? transaction = null)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return command;
        }
        bool Upgraded()
        {
            using var info = Command("SELECT count(*) FROM pragma_table_info('accounts') WHERE name = 'salt'");
            return (long)info.ExecuteScalar()! == 0;
        }
        if (Upgraded()) return;
        using (var off = Command("PRAGMA foreign_keys = OFF")) off.ExecuteNonQuery();
        try
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            using (var info = Command("SELECT count(*) FROM pragma_table_info('accounts') WHERE name = 'salt'", transaction))
                if ((long)info.ExecuteScalar()! == 0) return;
            var rows = new List<(string Id, string Name, long Created)>();
            using (var select = Command("""
                SELECT a.id, a.username, a.created_at FROM accounts a
                LEFT JOIN (SELECT account_id, max(expires_at) AS last FROM relay_sessions GROUP BY account_id) s ON s.account_id = a.id
                ORDER BY coalesce(s.last, 0) DESC, a.created_at DESC, a.id
                """, transaction))
            using (var reader = select.ExecuteReader())
                while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            var used = new HashSet<string>(StringComparer.Ordinal);
            var accounts = new List<(string Id, string Name, string Key, long Created)>();
            var renamed = new List<(string Id, string Name, long Created)>();
            foreach (var row in rows)
            {
                var key = UsernameKey(row.Name) ?? row.Name.ToUpperInvariant();
                if (used.Add(key)) accounts.Add((row.Id, row.Name, key, row.Created));
                else renamed.Add(row);
            }
            foreach (var row in renamed)
            {
                for (var number = 2; ; number++)
                {
                    var name = WithSuffix(row.Name, number);
                    var key = UsernameKey(name) ?? name.ToUpperInvariant();
                    if (!used.Add(key)) continue;
                    accounts.Add((row.Id, name, key, row.Created));
                    break;
                }
            }
            using (var create = Command("""
                DROP INDEX IF EXISTS accounts_username;
                DROP INDEX IF EXISTS accounts_username_key;
                CREATE TABLE accounts_upgraded (
                    id TEXT PRIMARY KEY, username TEXT NOT NULL,
                    username_key TEXT NOT NULL UNIQUE, created_at INTEGER NOT NULL
                );
                """, transaction))
                create.ExecuteNonQuery();
            using (var insert = Command("INSERT INTO accounts_upgraded VALUES ($id, $name, $key, $created)", transaction))
            {
                var id = insert.Parameters.Add("$id", SqliteType.Text);
                var name = insert.Parameters.Add("$name", SqliteType.Text);
                var key = insert.Parameters.Add("$key", SqliteType.Text);
                var created = insert.Parameters.Add("$created", SqliteType.Integer);
                foreach (var account in accounts)
                {
                    (id.Value, name.Value, key.Value, created.Value) = (account.Id, account.Name, account.Key, account.Created);
                    insert.ExecuteNonQuery();
                }
            }
            using (var swap = Command("DROP TABLE accounts; ALTER TABLE accounts_upgraded RENAME TO accounts;", transaction))
                swap.ExecuteNonQuery();
            using (var check = Command("PRAGMA foreign_key_check", transaction))
            using (var reader = check.ExecuteReader())
                if (reader.Read()) throw new InvalidDataException("Account upgrade left sessions without an account.");
            transaction.Commit();
        }
        finally
        {
            using var on = Command("PRAGMA foreign_keys = ON");
            on.ExecuteNonQuery();
        }
    }

    private static string WithSuffix(string name, int number)
    {
        var suffix = "#" + number.ToString(CultureInfo.InvariantCulture);
        var elements = new StringInfo(name);
        var count = elements.LengthInTextElements;
        var stem = name;
        while (count > 0 && stem.Length + suffix.Length > MaxUsernameLength)
            stem = elements.SubstringByTextElements(0, --count);
        return stem.TrimEnd() + suffix;
    }

    private static byte[] HashToken(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));
    public sealed record LoginResult(string AccountId, string Username, string SessionToken, long ExpiresAt, bool Created);
}
