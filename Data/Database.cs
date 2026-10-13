using MailMover.Models;
using Microsoft.Data.Sqlite;

namespace MailMover.Data;

public static class Database
{
    internal static readonly string DbFileName = "mailmover.db";
    private static readonly string ConnectionString = $"Data Source={DbFileName}";

    public static void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
        Migrate(connection);
    }

    // Colonne aggiunte dopo la prima versione: i database esistenti non le hanno.
    private static void Migrate(SqliteConnection connection)
    {
        if (!ColumnExists(connection, "accounts", "ignore_ssl_errors"))
            Execute(connection, "ALTER TABLE accounts ADD COLUMN ignore_ssl_errors INTEGER NOT NULL DEFAULT 0");

        if (!ColumnExists(connection, "accounts", "ssh_tunnel_enabled"))
            Execute(connection, "ALTER TABLE accounts ADD COLUMN ssh_tunnel_enabled INTEGER NOT NULL DEFAULT 0");
        if (!ColumnExists(connection, "accounts", "ssh_host"))
            Execute(connection, "ALTER TABLE accounts ADD COLUMN ssh_host TEXT NOT NULL DEFAULT ''");
        if (!ColumnExists(connection, "accounts", "ssh_port"))
            Execute(connection, "ALTER TABLE accounts ADD COLUMN ssh_port INTEGER NOT NULL DEFAULT 22");
        if (!ColumnExists(connection, "accounts", "ssh_user"))
            Execute(connection, "ALTER TABLE accounts ADD COLUMN ssh_user TEXT NOT NULL DEFAULT ''");
        if (!ColumnExists(connection, "accounts", "ssh_password"))
            Execute(connection, "ALTER TABLE accounts ADD COLUMN ssh_password TEXT NOT NULL DEFAULT ''");

        if (!ColumnExists(connection, "messages", "keywords"))
            Execute(connection, "ALTER TABLE messages ADD COLUMN keywords TEXT");
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table})";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private const string AccountColumns =
        "a.id, a.name, a.host, a.port, a.username, a.password, a.security, a.ignore_ssl_errors, " +
        "a.ssh_tunnel_enabled, a.ssh_host, a.ssh_port, a.ssh_user, a.ssh_password";

    public static List<Account> GetAccounts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {AccountColumns} FROM accounts a ORDER BY a.name";
        return ReadAccounts(command);
    }

    public static List<Account> GetAccountsWithBackup()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT DISTINCT {AccountColumns}
            FROM accounts a
            JOIN messages m ON m.account_id = a.id
            ORDER BY a.name
            """;
        return ReadAccounts(command);
    }

    public static void AddAccount(Account account)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounts (name, host, port, username, password, security, ignore_ssl_errors,
                                  ssh_tunnel_enabled, ssh_host, ssh_port, ssh_user, ssh_password)
            VALUES (@name, @host, @port, @username, @password, @security, @ignore_ssl,
                    @tunnel_enabled, @ssh_host, @ssh_port, @ssh_user, @ssh_password)
            """;
        command.Parameters.AddWithValue("@name", account.Name);
        command.Parameters.AddWithValue("@host", account.Host);
        command.Parameters.AddWithValue("@port", account.Port);
        command.Parameters.AddWithValue("@username", account.Username);
        command.Parameters.AddWithValue("@password", account.Password);
        command.Parameters.AddWithValue("@security", account.Security);
        command.Parameters.AddWithValue("@ignore_ssl", account.IgnoreSslErrors ? 1 : 0);
        AddTunnelParameters(command, account.Tunnel);
        command.ExecuteNonQuery();
    }

    public static void UpdateAccount(Account account)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE accounts
            SET name = @name, host = @host, port = @port, username = @username,
                password = @password, security = @security, ignore_ssl_errors = @ignore_ssl,
                ssh_tunnel_enabled = @tunnel_enabled, ssh_host = @ssh_host, ssh_port = @ssh_port,
                ssh_user = @ssh_user, ssh_password = @ssh_password
            WHERE id = @id
            """;
        command.Parameters.AddWithValue("@name", account.Name);
        command.Parameters.AddWithValue("@host", account.Host);
        command.Parameters.AddWithValue("@port", account.Port);
        command.Parameters.AddWithValue("@username", account.Username);
        command.Parameters.AddWithValue("@password", account.Password);
        command.Parameters.AddWithValue("@security", account.Security);
        command.Parameters.AddWithValue("@ignore_ssl", account.IgnoreSslErrors ? 1 : 0);
        AddTunnelParameters(command, account.Tunnel);
        command.Parameters.AddWithValue("@id", account.Id);
        command.ExecuteNonQuery();
    }

    private static void AddTunnelParameters(SqliteCommand command, SshTunnelConfig? tunnel)
    {
        command.Parameters.AddWithValue("@tunnel_enabled", tunnel?.Enabled == true ? 1 : 0);
        command.Parameters.AddWithValue("@ssh_host", tunnel?.Host ?? string.Empty);
        command.Parameters.AddWithValue("@ssh_port", tunnel?.Port ?? SshTunnelConfig.DefaultPort);
        command.Parameters.AddWithValue("@ssh_user", tunnel?.User ?? string.Empty);
        command.Parameters.AddWithValue("@ssh_password", tunnel?.Password ?? string.Empty);
    }

    public static void SetIgnoreSslErrors(int id, bool value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE accounts SET ignore_ssl_errors = @value WHERE id = @id";
        command.Parameters.AddWithValue("@value", value ? 1 : 0);
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    public static void DeleteAccount(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM accounts WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    public static Dictionary<int, long> GetBackupCountsPerAccount()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_id, COUNT(*) FROM messages GROUP BY account_id";

        var counts = new Dictionary<int, long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            counts[reader.GetInt32(0)] = reader.GetInt64(1);
        return counts;
    }

    public static long CountBackedUpMessages(int accountId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages WHERE account_id = @account";
        command.Parameters.AddWithValue("@account", accountId);
        return (long)command.ExecuteScalar()!;
    }

    public static void DeleteBackup(int accountId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM messages WHERE account_id = @account";
        command.Parameters.AddWithValue("@account", accountId);
        command.ExecuteNonQuery();
    }

    public static string? GetBackupDelimiter(int accountId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT folder_delimiter FROM backup_meta WHERE account_id = @account";
        command.Parameters.AddWithValue("@account", accountId);
        return command.ExecuteScalar() as string;
    }

    public static void UpsertBackupMeta(int accountId, string folderDelimiter)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO backup_meta (account_id, folder_delimiter, last_backup_at)
            VALUES (@account, @delimiter, datetime('now'))
            ON CONFLICT(account_id) DO UPDATE SET
                folder_delimiter = @delimiter,
                last_backup_at = datetime('now')
            """;
        command.Parameters.AddWithValue("@account", accountId);
        command.Parameters.AddWithValue("@delimiter", folderDelimiter);
        command.ExecuteNonQuery();
    }

    public static HashSet<string> GetExistingUids(SqliteConnection connection, int accountId, string folderPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT uid FROM messages WHERE account_id = @account AND folder_path = @folder AND uid IS NOT NULL";
        command.Parameters.AddWithValue("@account", accountId);
        command.Parameters.AddWithValue("@folder", folderPath);

        var uids = new HashSet<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            uids.Add(reader.GetString(0));
        return uids;
    }

    public static int InsertMessage(
        SqliteConnection connection, SqliteTransaction? transaction,
        int accountId, string folderPath, string uid, string? messageId, string? subject,
        string? fromAddress, string? internalDate, string? flags, string? keywords, long size, string hash, byte[] raw)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO messages
                (account_id, folder_path, uid, message_id, subject, from_address,
                 internal_date, flags, keywords, size, hash, raw)
            VALUES (@account, @folder, @uid, @message_id, @subject, @from,
                    @date, @flags, @keywords, @size, @hash, @raw)
            """;
        command.Parameters.AddWithValue("@account", accountId);
        command.Parameters.AddWithValue("@folder", folderPath);
        command.Parameters.AddWithValue("@uid", uid);
        command.Parameters.AddWithValue("@message_id", (object?)messageId ?? DBNull.Value);
        command.Parameters.AddWithValue("@subject", (object?)subject ?? DBNull.Value);
        command.Parameters.AddWithValue("@from", (object?)fromAddress ?? DBNull.Value);
        command.Parameters.AddWithValue("@date", (object?)internalDate ?? DBNull.Value);
        command.Parameters.AddWithValue("@flags", (object?)flags ?? DBNull.Value);
        command.Parameters.AddWithValue("@keywords", (object?)keywords ?? DBNull.Value);
        command.Parameters.AddWithValue("@size", size);
        command.Parameters.AddWithValue("@hash", hash);
        command.Parameters.AddWithValue("@raw", raw);
        return command.ExecuteNonQuery();
    }

    public static List<string> GetBackupFolderPaths(int accountId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT folder_path FROM messages WHERE account_id = @account ORDER BY folder_path";
        command.Parameters.AddWithValue("@account", accountId);

        var paths = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            paths.Add(reader.GetString(0));
        return paths;
    }

    public static long CountPendingCopies(int sourceAccountId, int destinationAccountId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM messages m
            WHERE m.account_id = @source
              AND NOT EXISTS (
                    SELECT 1 FROM copy_log c
                    WHERE c.mail_pk = m.id AND c.dest_account_id = @dest)
            """;
        command.Parameters.AddWithValue("@source", sourceAccountId);
        command.Parameters.AddWithValue("@dest", destinationAccountId);
        return (long)command.ExecuteScalar()!;
    }

    public static long CountPendingInFolder(SqliteConnection connection, int sourceAccountId, string folderPath, int destinationAccountId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM messages m
            WHERE m.account_id = @source AND m.folder_path = @folder
              AND NOT EXISTS (
                    SELECT 1 FROM copy_log c
                    WHERE c.mail_pk = m.id AND c.dest_account_id = @dest)
            """;
        command.Parameters.AddWithValue("@source", sourceAccountId);
        command.Parameters.AddWithValue("@folder", folderPath);
        command.Parameters.AddWithValue("@dest", destinationAccountId);
        return (long)command.ExecuteScalar()!;
    }

    public static HashSet<long> GetCopiedMailPks(int destinationAccountId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT mail_pk FROM copy_log WHERE dest_account_id = @dest";
        command.Parameters.AddWithValue("@dest", destinationAccountId);

        var pks = new HashSet<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            pks.Add(reader.GetInt64(0));
        return pks;
    }

    public static void LogCopy(long mailPk, int destinationAccountId, SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO copy_log (mail_pk, dest_account_id) VALUES (@pk, @dest)";
        command.Parameters.AddWithValue("@pk", mailPk);
        command.Parameters.AddWithValue("@dest", destinationAccountId);
        command.ExecuteNonQuery();
    }

    private static List<Account> ReadAccounts(SqliteCommand command)
    {
        var accounts = new List<Account>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            bool tunnelEnabled = reader.GetBoolean(8);
            string sshHost = reader.GetString(9);
            SshTunnelConfig? tunnel = tunnelEnabled || sshHost.Length > 0
                ? new SshTunnelConfig(tunnelEnabled, sshHost, reader.GetInt32(10), reader.GetString(11), reader.GetString(12))
                : null;

            accounts.Add(new Account(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetBoolean(7),
                tunnel));
        }
        return accounts;
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS accounts (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            name              TEXT NOT NULL,
            host              TEXT NOT NULL,
            port              INTEGER NOT NULL,
            username          TEXT NOT NULL,
            password          TEXT NOT NULL,
            security          INTEGER NOT NULL DEFAULT 2,
            ignore_ssl_errors INTEGER NOT NULL DEFAULT 0,
            ssh_tunnel_enabled INTEGER NOT NULL DEFAULT 0,
            ssh_host           TEXT NOT NULL DEFAULT '',
            ssh_port           INTEGER NOT NULL DEFAULT 22,
            ssh_user           TEXT NOT NULL DEFAULT '',
            ssh_password       TEXT NOT NULL DEFAULT '',
            created_at        TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS backup_meta (
            account_id       INTEGER PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
            folder_delimiter TEXT NOT NULL DEFAULT '/',
            last_backup_at   TEXT
        );

        CREATE TABLE IF NOT EXISTS messages (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id    INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            folder_path   TEXT NOT NULL,
            uid           TEXT,
            message_id    TEXT,
            subject       TEXT,
            from_address  TEXT,
            internal_date TEXT,
            flags         TEXT,
            keywords      TEXT,
            size          INTEGER NOT NULL DEFAULT 0,
            hash          TEXT NOT NULL,
            raw           BLOB NOT NULL,
            backed_up_at  TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE (account_id, folder_path, hash)
        );

        CREATE TABLE IF NOT EXISTS copy_log (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            mail_pk         INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
            dest_account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            copied_at       TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE (mail_pk, dest_account_id)
        );

        CREATE INDEX IF NOT EXISTS idx_messages_account_folder ON messages (account_id, folder_path);
        CREATE INDEX IF NOT EXISTS idx_copy_log_dest ON copy_log (dest_account_id);
        """;
}
