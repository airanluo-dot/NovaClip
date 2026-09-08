using System.Globalization;
using Microsoft.Data.Sqlite;
using NovaClip.Core;

namespace NovaClip.Infrastructure;

public sealed class SqliteDownloadTaskRepository : IDownloadTaskRepository, IHistoryRepository, IDurableObligationStore, IDisposable
{
    private const int CurrentSchemaVersion = 3;
    private const int DefaultPageSize = 200;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public SqliteDownloadTaskRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync(async token =>
        {
            await using var connection = await OpenAsync(token).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection, transaction, """
                CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL);
                INSERT INTO SchemaVersion (Version)
                SELECT 0 WHERE NOT EXISTS (SELECT 1 FROM SchemaVersion);
                """, token).ConfigureAwait(false);

            var version = await ReadSchemaVersionAsync(connection, transaction, token).ConfigureAwait(false);
            if (version < 1)
            {
                await ExecuteNonQueryAsync(connection, transaction, """
                    CREATE TABLE IF NOT EXISTS DownloadTasks (
                        Id TEXT PRIMARY KEY,
                        PageUrl TEXT NOT NULL,
                        Title TEXT NOT NULL,
                        Status INTEGER NOT NULL,
                        OperationState INTEGER NOT NULL DEFAULT 0,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL,
                        OutputPath TEXT NOT NULL,
                        SelectedQualityId INTEGER NULL,
                        SelectedCodec TEXT NULL,
                        ErrorCode TEXT NULL,
                        ErrorMessage TEXT NULL,
                        DownloadedBytes INTEGER NOT NULL DEFAULT 0,
                        TotalBytes INTEGER NULL,
                        RunId INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE TABLE IF NOT EXISTS DownloadHistory (
                        Id TEXT PRIMARY KEY,
                        PageUrl TEXT NOT NULL,
                        Title TEXT NOT NULL,
                        Status INTEGER NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL,
                        OutputPath TEXT NOT NULL,
                        SelectedQualityId INTEGER NULL,
                        SelectedCodec TEXT NULL,
                        ErrorCode TEXT NULL,
                        ErrorMessage TEXT NULL,
                        DownloadedBytes INTEGER NOT NULL DEFAULT 0,
                        TotalBytes INTEGER NULL,
                        RunId INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE TABLE IF NOT EXISTS DurableObligations (
                        Id TEXT PRIMARY KEY,
                        Kind INTEGER NOT NULL,
                        Payload TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL,
                        Attempts INTEGER NOT NULL DEFAULT 0,
                        LastError TEXT NULL,
                        CompletedAt TEXT NULL
                    );
                    """, token).ConfigureAwait(false);
                await SetSchemaVersionAsync(connection, transaction, 1, token).ConfigureAwait(false);
                version = 1;
            }

            if (version < 2)
            {
                await EnsureColumnAsync(connection, transaction, "DownloadTasks", "RunId", "INTEGER NOT NULL DEFAULT 0", token).ConfigureAwait(false);
                await EnsureColumnAsync(connection, transaction, "DownloadHistory", "RunId", "INTEGER NOT NULL DEFAULT 0", token).ConfigureAwait(false);
                await ExecuteNonQueryAsync(connection, transaction, """
                    CREATE INDEX IF NOT EXISTS IX_DownloadTasks_UpdatedAt_Id ON DownloadTasks (UpdatedAt DESC, Id DESC);
                    CREATE INDEX IF NOT EXISTS IX_DownloadHistory_UpdatedAt_Id ON DownloadHistory (UpdatedAt DESC, Id DESC);
                    CREATE INDEX IF NOT EXISTS IX_DurableObligations_Pending ON DurableObligations (CompletedAt, UpdatedAt);
                    """, token).ConfigureAwait(false);
                await SetSchemaVersionAsync(connection, transaction, 2, token).ConfigureAwait(false);
            }

            if (version < 3)
            {
                await EnsureColumnAsync(connection, transaction, "DownloadTasks", "OperationState", "INTEGER NOT NULL DEFAULT 0", token).ConfigureAwait(false);
                await EnsureColumnAsync(connection, transaction, "DownloadHistory", "OperationState", "INTEGER NOT NULL DEFAULT 0", token).ConfigureAwait(false);
                await SetSchemaVersionAsync(connection, transaction, CurrentSchemaVersion, token).ConfigureAwait(false);
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task UpsertAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default) =>
        UpsertIntoAsync("DownloadTasks", snapshot, cancellationToken);

    public Task AddAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default) =>
        UpsertIntoAsync("DownloadHistory", snapshot, cancellationToken);

    public Task<DownloadTaskSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetFromAsync("DownloadTasks", id, cancellationToken);

    public Task<IReadOnlyList<DownloadTaskSnapshot>> GetAllAsync(CancellationToken cancellationToken = default) =>
        GetAllFromAsync("DownloadTasks", cancellationToken);

    async Task<IReadOnlyList<DownloadTaskSnapshot>> IHistoryRepository.GetAllAsync(CancellationToken cancellationToken) =>
        await GetAllFromAsync("DownloadHistory", cancellationToken).ConfigureAwait(false);

    public Task<DownloadPage> GetPageAsync(int limit, DateTimeOffset? beforeUpdatedAt = null, Guid? beforeId = null, CancellationToken cancellationToken = default) =>
        GetPageFromAsync("DownloadTasks", limit, beforeUpdatedAt, beforeId, cancellationToken);

    async Task<DownloadPage> IHistoryRepository.GetPageAsync(int limit, DateTimeOffset? beforeUpdatedAt, Guid? beforeId, CancellationToken cancellationToken) =>
        await GetPageFromAsync("DownloadHistory", limit, beforeUpdatedAt, beforeId, cancellationToken).ConfigureAwait(false);

    public async Task RemoveAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync(async token =>
        {
            await using var connection = await OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM DownloadHistory WHERE Id = $id";
            command.Parameters.AddWithValue("$id", taskId.ToString("D"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(DurableObligationKind kind, string payload, string? errorMessage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        await ExecuteWriteAsync(async token =>
        {
            await using var connection = await OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO DurableObligations (Id, Kind, Payload, CreatedAt, UpdatedAt, Attempts, LastError, CompletedAt)
                VALUES ($id, $kind, $payload, $created, $updated, 0, $error, NULL);
                """;
            var now = DateTimeOffset.UtcNow.ToString("O");
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$kind", (int)kind);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$created", now);
            command.Parameters.AddWithValue("$updated", now);
            command.Parameters.AddWithValue("$error", (object?)errorMessage ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DurableObligation>> GetPendingAsync(int limit, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var result = new List<DurableObligation>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Kind, Payload, CreatedAt, UpdatedAt, Attempts, LastError
            FROM DurableObligations
            WHERE CompletedAt IS NULL
            ORDER BY UpdatedAt ASC, Id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParse(reader.GetString(0), out var id) || !Enum.IsDefined(typeof(DurableObligationKind), reader.GetInt32(1))) continue;
            if (!DateTimeOffset.TryParse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created) ||
                !DateTimeOffset.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updated)) continue;
            result.Add(new DurableObligation(id, (DurableObligationKind)reader.GetInt32(1), reader.GetString(2), created, updated, reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return result;
    }

    public Task CompleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        UpdateObligationAsync(id, completed: true, null, cancellationToken);

    public Task RecordFailureAsync(Guid id, string errorMessage, CancellationToken cancellationToken = default) =>
        UpdateObligationAsync(id, completed: false, errorMessage, cancellationToken);

    private async Task UpdateObligationAsync(Guid id, bool completed, string? error, CancellationToken cancellationToken)
    {
        await ExecuteWriteAsync(async token =>
        {
            await using var connection = await OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = completed
                ? "UPDATE DurableObligations SET CompletedAt = $completed, UpdatedAt = $updated WHERE Id = $id"
                : "UPDATE DurableObligations SET Attempts = Attempts + 1, LastError = $error, UpdatedAt = $updated WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            if (completed) command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
            else command.Parameters.AddWithValue("$error", error ?? "unknown");
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task UpsertIntoAsync(string table, DownloadTaskSnapshot snapshot, CancellationToken cancellationToken) =>
        ExecuteWriteAsync(async token =>
        {
            ValidateTableName(table);
            await using var connection = await OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO {table} (Id, PageUrl, Title, Status, OperationState, CreatedAt, UpdatedAt, OutputPath, SelectedQualityId, SelectedCodec, ErrorCode, ErrorMessage, DownloadedBytes, TotalBytes, RunId)
                VALUES ($id, $pageUrl, $title, $status, $operationState, $createdAt, $updatedAt, $outputPath, $quality, $codec, $errorCode, $errorMessage, $downloaded, $total, $runId)
                ON CONFLICT(Id) DO UPDATE SET
                    PageUrl = excluded.PageUrl,
                    Title = excluded.Title,
                    Status = excluded.Status,
                    OperationState = excluded.OperationState,
                    UpdatedAt = excluded.UpdatedAt,
                    OutputPath = excluded.OutputPath,
                    SelectedQualityId = excluded.SelectedQualityId,
                    SelectedCodec = excluded.SelectedCodec,
                    ErrorCode = excluded.ErrorCode,
                    ErrorMessage = excluded.ErrorMessage,
                    DownloadedBytes = excluded.DownloadedBytes,
                    TotalBytes = excluded.TotalBytes,
                    RunId = excluded.RunId
                WHERE excluded.UpdatedAt >= {table}.UpdatedAt;
                """;
            AddParameters(command, snapshot);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<DownloadTaskSnapshot?> GetFromAsync(string table, Guid id, CancellationToken cancellationToken)
    {
        ValidateTableName(table);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id, PageUrl, Title, Status, OperationState, CreatedAt, UpdatedAt, OutputPath, SelectedQualityId, SelectedCodec, ErrorCode, ErrorMessage, DownloadedBytes, TotalBytes, RunId FROM {table} WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSnapshot(reader) : null;
    }

    private async Task<IReadOnlyList<DownloadTaskSnapshot>> GetAllFromAsync(string table, CancellationToken cancellationToken)
    {
        var result = new List<DownloadTaskSnapshot>();
        DateTimeOffset? beforeUpdatedAt = null;
        Guid? beforeId = null;
        while (true)
        {
            var page = await GetPageFromAsync(table, DefaultPageSize, beforeUpdatedAt, beforeId, cancellationToken).ConfigureAwait(false);
            result.AddRange(page.Items);
            if (!page.HasMore || page.Items.Count == 0) return result;
            beforeUpdatedAt = page.NextUpdatedAt;
            beforeId = page.NextId;
        }
    }

    private async Task<DownloadPage> GetPageFromAsync(string table, int limit, DateTimeOffset? beforeUpdatedAt, Guid? beforeId, CancellationToken cancellationToken)
    {
        ValidateTableName(table);
        limit = Math.Clamp(limit, 1, 1000);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var cursorClause = beforeUpdatedAt is null || beforeId is null
            ? string.Empty
            : "WHERE (UpdatedAt < $beforeUpdatedAt OR (UpdatedAt = $beforeUpdatedAt AND Id < $beforeId))";
        command.CommandText = $"""
            SELECT Id, PageUrl, Title, Status, OperationState, CreatedAt, UpdatedAt, OutputPath, SelectedQualityId, SelectedCodec, ErrorCode, ErrorMessage, DownloadedBytes, TotalBytes, RunId
            FROM {table}
            {cursorClause}
            ORDER BY UpdatedAt DESC, Id DESC
            LIMIT $limit;
            """;
        if (beforeUpdatedAt is not null && beforeId is not null)
        {
            command.Parameters.AddWithValue("$beforeUpdatedAt", beforeUpdatedAt.Value.ToString("O"));
            command.Parameters.AddWithValue("$beforeId", beforeId.Value.ToString("D"));
        }
        command.Parameters.AddWithValue("$limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<DownloadTaskSnapshot>(limit);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = ReadSnapshot(reader);
            if (snapshot is not null) result.Add(snapshot);
        }
        var hasMore = result.Count > limit;
        if (hasMore) result.RemoveAt(result.Count - 1);
        var last = result.LastOrDefault();
        return new DownloadPage(result, hasMore ? last?.UpdatedAt : null, hasMore ? last?.Id : null, hasMore);
    }

    private static void AddParameters(SqliteCommand command, DownloadTaskSnapshot snapshot)
    {
        command.Parameters.AddWithValue("$id", snapshot.Id.ToString("D"));
        command.Parameters.AddWithValue("$pageUrl", snapshot.PageUrl);
        command.Parameters.AddWithValue("$title", snapshot.Title);
        command.Parameters.AddWithValue("$status", (int)snapshot.State);
        command.Parameters.AddWithValue("$operationState", (int)snapshot.OperationState);
        command.Parameters.AddWithValue("$createdAt", snapshot.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", snapshot.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$outputPath", snapshot.OutputPath);
        command.Parameters.AddWithValue("$quality", (object?)snapshot.SelectedQualityId ?? DBNull.Value);
        command.Parameters.AddWithValue("$codec", (object?)snapshot.SelectedCodec ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", (object?)snapshot.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)snapshot.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$downloaded", snapshot.DownloadedBytes);
        command.Parameters.AddWithValue("$total", (object?)snapshot.TotalBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$runId", snapshot.RunId);
    }

    private static DownloadTaskSnapshot? ReadSnapshot(SqliteDataReader reader)
    {
        try
        {
            if (!Guid.TryParse(reader.GetString(0), out var id) ||
                !DateTimeOffset.TryParse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt) ||
                !DateTimeOffset.TryParse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt)) return null;
            var stateValue = reader.GetInt32(3);
            var state = Enum.IsDefined(typeof(DownloadTaskState), stateValue) ? (DownloadTaskState)stateValue : DownloadTaskState.Failed;
            var operationValue = reader.GetInt32(4);
            var operationState = Enum.IsDefined(typeof(DurableOperationState), operationValue) ? (DurableOperationState)operationValue : DurableOperationState.Failed;
            return new DownloadTaskSnapshot
            {
                Id = id,
                PageUrl = reader.GetString(1),
                Title = reader.GetString(2),
                State = state,
                OperationState = operationState,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                OutputPath = reader.GetString(7),
                SelectedQualityId = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                SelectedCodec = reader.IsDBNull(9) ? null : reader.GetString(9),
                ErrorCode = reader.IsDBNull(10) ? null : reader.GetString(10),
                ErrorMessage = reader.IsDBNull(11) ? null : reader.GetString(11),
                DownloadedBytes = Math.Max(0, reader.GetInt64(12)),
                TotalBytes = reader.IsDBNull(13) ? null : reader.GetInt64(13),
                RunId = reader.IsDBNull(14) ? 0 : Math.Max(0, reader.GetInt64(14))
            };
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or IndexOutOfRangeException or OverflowException)
        {
            System.Diagnostics.Debug.WriteLine($"NovaClip skipped malformed persisted data: {exception.Message}");
            return null;
        }
    }

    private async Task ExecuteWriteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await operation(cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Version FROM SchemaVersion LIMIT 1";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long longValue ? (int)longValue : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task SetSchemaVersionAsync(SqliteConnection connection, SqliteTransaction transaction, int version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE SchemaVersion SET Version = $version";
        command.Parameters.AddWithValue("$version", version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string definition, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table})";
        var exists = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            await ExecuteNonQueryAsync(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {definition}", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateTableName(string table)
    {
        if (table is not ("DownloadTasks" or "DownloadHistory")) throw new ArgumentException("Unsupported persistence table.", nameof(table));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
