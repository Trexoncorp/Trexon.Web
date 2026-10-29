using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Trexon.Web.Users;

/// <summary>A persistent, parameterized SQLite account store suitable for a single service deployment.</summary>
public sealed class SqliteUserStore : IUserStore
{
    private readonly string _connectionString;
    private readonly string _directory;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private bool _initialized;
    /// <summary>Configures a database path without performing I/O. Call InitializeAsync before use.</summary>
    public SqliteUserStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (databasePath == ":memory:") throw new ArgumentException("Use a persistent or temporary file path.", nameof(databasePath));
        var path = Path.GetFullPath(databasePath);
        _directory = Path.GetDirectoryName(path)!;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, DefaultTimeout = 15, Pooling = false }.ToString();
    }
    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(_directory);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS trexon_users (
                id TEXT PRIMARY KEY, username TEXT NOT NULL, normalized_name TEXT NOT NULL UNIQUE,
                password_hash TEXT NOT NULL, roles TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS trexon_refresh (
                token_hash TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES trexon_users(id),
                family_id TEXT NOT NULL, expires_at INTEGER NOT NULL, used INTEGER NOT NULL DEFAULT 0,
                revoked INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_trexon_refresh_family ON trexon_refresh(family_id);
            CREATE INDEX IF NOT EXISTS ix_trexon_refresh_user ON trexon_refresh(user_id);
            """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally { _initialization.Release(); }
    }
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_initialized) await InitializeAsync(token);
        var connection = new SqliteConnection(_connectionString);
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return command;
    }
    private static async Task<StoredUser?> ReadUser(SqliteCommand command, CancellationToken token)
    {
        using (command)
        await using (var reader = await command.ExecuteReaderAsync(token))
            return await reader.ReadAsync(token) ? new StoredUser(reader.GetString(0), reader.GetString(1), reader.GetString(2), JsonSerializer.Deserialize<string[]>(reader.GetString(3))!) : null;
    }
    /// <inheritdoc />
    public async Task<bool> TryCreateAsync(StoredUser user, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, "INSERT INTO trexon_users (id,username,normalized_name,password_hash,roles) VALUES ($id,$name,$normalized,$password,$roles)", null,
            ("$id", user.Id), ("$name", user.Username), ("$normalized", user.Username.ToUpperInvariant()), ("$password", user.PasswordHash), ("$roles", JsonSerializer.Serialize(user.Roles)));
        try { await command.ExecuteNonQueryAsync(cancellationToken); return true; }
        catch (SqliteException error) when (error.SqliteErrorCode == 19) { return false; }
    }
    /// <inheritdoc />
    public Task<StoredUser?> FindByNameAsync(string username, CancellationToken cancellationToken = default) => Find("normalized_name", username.ToUpperInvariant(), cancellationToken);
    /// <inheritdoc />
    public Task<StoredUser?> FindByIdAsync(string id, CancellationToken cancellationToken = default) => Find("id", id, cancellationToken);
    private async Task<StoredUser?> Find(string column, string value, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        return await ReadUser(Command(connection, $"SELECT id,username,password_hash,roles FROM trexon_users WHERE {column}=$value", null, ("$value", value)), token);
    }
    /// <inheritdoc />
    public async Task SetRolesAsync(string id, string[] roles, CancellationToken cancellationToken = default)
    {
        if (roles.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Role names cannot be empty.", nameof(roles));
        await using var connection = await OpenAsync(cancellationToken); await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = Command(connection, "UPDATE trexon_users SET roles=$roles WHERE id=$id; UPDATE trexon_refresh SET revoked=1 WHERE user_id=$id", transaction,
            ("$roles", JsonSerializer.Serialize(roles.Distinct().ToArray())), ("$id", id));
        await command.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }
    private static SqliteCommand InsertSession(SqliteConnection connection, RefreshSession session, SqliteTransaction? transaction = null) =>
        Command(connection, "INSERT INTO trexon_refresh(token_hash,user_id,family_id,expires_at) VALUES($hash,$user,$family,$expires)", transaction,
            ("$hash", session.TokenHash), ("$user", session.UserId), ("$family", session.FamilyId), ("$expires", session.ExpiresAt.ToUnixTimeSeconds()));
    /// <inheritdoc />
    public async Task AddSessionAsync(RefreshSession session, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = InsertSession(connection, session);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    /// <inheritdoc />
    public async Task<StoredUser?> RotateAsync(string oldHash, string nextHash, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        string userId, family; long expires; bool used, revoked;
        using (var read = Command(connection, "SELECT user_id,family_id,expires_at,used,revoked FROM trexon_refresh WHERE token_hash=$hash", transaction, ("$hash", oldHash)))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            userId = reader.GetString(0); family = reader.GetString(1); expires = reader.GetInt64(2);
            used = reader.GetBoolean(3); revoked = reader.GetBoolean(4);
        }
        if (used || revoked || expires <= now.ToUnixTimeSeconds())
        {
            using var revoke = Command(connection, "UPDATE trexon_refresh SET revoked=1 WHERE family_id=$family", transaction, ("$family", family));
            await revoke.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return null;
        }
        using (var consume = Command(connection, "UPDATE trexon_refresh SET used=1 WHERE token_hash=$hash", transaction, ("$hash", oldHash)))
            await consume.ExecuteNonQueryAsync(cancellationToken);
        using (var insert = InsertSession(connection, new RefreshSession(nextHash, userId, family, DateTimeOffset.FromUnixTimeSeconds(expires)), transaction))
            await insert.ExecuteNonQueryAsync(cancellationToken);
        var user = await ReadUser(Command(connection, "SELECT id,username,password_hash,roles FROM trexon_users WHERE id=$id", transaction, ("$id", userId)), cancellationToken);
        await transaction.CommitAsync(cancellationToken); return user;
    }
    /// <inheritdoc />
    public async Task RevokeAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, "UPDATE trexon_refresh SET revoked=1 WHERE family_id IN (SELECT family_id FROM trexon_refresh WHERE token_hash=$hash)", null, ("$hash", tokenHash));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetSessionExpiryAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, "SELECT expires_at FROM trexon_refresh WHERE token_hash=$hash", null, ("$hash", tokenHash));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long timestamp ? DateTimeOffset.FromUnixTimeSeconds(timestamp) : null;
    }
}
