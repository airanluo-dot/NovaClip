using NovaClip.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace NovaClip.Infrastructure;

public sealed class SqliteDownloadTaskRepository : IDownloadTaskRepository, IHistoryRepository, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SqliteDownloadTaskRepository(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync(async token =>
        {
            await using var connection = await OpenAsync(token).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS DownloadTasks (
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
                TotalBytes INTEGER NULL
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
                TotalBytes INTEGER NULL
            );
            """;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default) =>
        await UpsertIntoAsync("DownloadTasks", snapshot, cancellationToken).ConfigureAwait(false);

    public async Task AddAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default) =>
        await UpsertIntoAsync("DownloadHistory", snapshot, cancellationToken).ConfigureAwait(false);

    public Task<DownloadTaskSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetFromAsync("DownloadTasks", id, cancellationToken);

    public Task<IReadOnlyList<DownloadTaskSnapshot>> GetAllAsync(CancellationToken cancellationToken = default) =>
        GetAllFromAsync("DownloadTasks", cancellationToken);

    async Task<IReadOnlyList<DownloadTaskSnapshot>> IHistoryRepository.GetAllAsync(CancellationToken cancellationToken) =>
        await GetAllFromAsync("DownloadHistory", cancellationToken).ConfigureAwait(false);

    public async Task RemoveAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync(async token =>
        {
            await using var connection = await OpenAsync(token).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM DownloadHistory WHERE Id = $id";
            command.Parameters.AddWithValue("$id", taskId.ToString("D"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task UpsertIntoAsync(string table, DownloadTaskSnapshot snapshot, CancellationToken cancellationToken) => ExecuteWriteAsync(async token =>
    {
        ValidateTableName(table);
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {table} (Id, PageUrl, Title, Status, CreatedAt, UpdatedAt, OutputPath, SelectedQualityId, SelectedCodec, ErrorCode, ErrorMessage, DownloadedBytes, TotalBytes)
            VALUES ($id, $pageUrl, $title, $status, $createdAt, $updatedAt, $outputPath, $quality, $codec, $errorCode, $errorMessage, $downloaded, $total)
            ON CONFLICT(Id) DO UPDATE SET
                PageUrl = excluded.PageUrl, Title = excluded.Title, Status = excluded.Status,
                UpdatedAt = excluded.UpdatedAt, OutputPath = excluded.OutputPath,
                SelectedQualityId = excluded.SelectedQualityId, SelectedCodec = excluded.SelectedCodec,
                ErrorCode = excluded.ErrorCode, ErrorMessage = excluded.ErrorMessage,
                DownloadedBytes = excluded.DownloadedBytes, TotalBytes = excluded.TotalBytes
            WHERE excluded.UpdatedAt >= {table}.UpdatedAt;
            """;
        AddParameters(command, snapshot);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }, cancellationToken);

    private async Task<DownloadTaskSnapshot?> GetFromAsync(string table, Guid id, CancellationToken cancellationToken)
    {
        ValidateTableName(table);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id, PageUrl, Title, Status, CreatedAt, UpdatedAt, OutputPath, SelectedQualityId, SelectedCodec, ErrorCode, ErrorMessage, DownloadedBytes, TotalBytes FROM {table} WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSnapshot(reader) : null;
    }

    private async Task<IReadOnlyList<DownloadTaskSnapshot>> GetAllFromAsync(string table, CancellationToken cancellationToken)
    {
        ValidateTableName(table);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id, PageUrl, Title, Status, CreatedAt, UpdatedAt, OutputPath, SelectedQualityId, SelectedCodec, ErrorCode, ErrorMessage, DownloadedBytes, TotalBytes FROM {table} ORDER BY UpdatedAt DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<DownloadTaskSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = ReadSnapshot(reader);
            if (snapshot is not null) result.Add(snapshot);
        }
        return result;
    }

    private static void AddParameters(SqliteCommand command, DownloadTaskSnapshot snapshot)
    {
        command.Parameters.AddWithValue("$id", snapshot.Id.ToString("D"));
        command.Parameters.AddWithValue("$pageUrl", snapshot.PageUrl);
        command.Parameters.AddWithValue("$title", snapshot.Title);
        command.Parameters.AddWithValue("$status", (int)snapshot.State);
        command.Parameters.AddWithValue("$createdAt", snapshot.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", snapshot.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$outputPath", snapshot.OutputPath);
        command.Parameters.AddWithValue("$quality", (object?)snapshot.SelectedQualityId ?? DBNull.Value);
        command.Parameters.AddWithValue("$codec", (object?)snapshot.SelectedCodec ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", (object?)snapshot.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)snapshot.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$downloaded", snapshot.DownloadedBytes);
        command.Parameters.AddWithValue("$total", (object?)snapshot.TotalBytes ?? DBNull.Value);
    }

    private static DownloadTaskSnapshot? ReadSnapshot(SqliteDataReader reader)
    {
        try
        {
            if (!Guid.TryParse(reader.GetString(0), out var id) ||
                !DateTimeOffset.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var createdAt) ||
                !DateTimeOffset.TryParse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var updatedAt)) return null;
            var stateValue = reader.GetInt32(3);
            var state = Enum.IsDefined(typeof(DownloadTaskState), stateValue) ? (DownloadTaskState)stateValue : DownloadTaskState.Failed;
            return new DownloadTaskSnapshot
            {
                Id = id,
                PageUrl = reader.GetString(1),
                Title = reader.GetString(2),
                State = state,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                OutputPath = reader.GetString(6),
                SelectedQualityId = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                SelectedCodec = reader.IsDBNull(8) ? null : reader.GetString(8),
                ErrorCode = reader.IsDBNull(9) ? null : reader.GetString(9),
                ErrorMessage = reader.IsDBNull(10) ? null : reader.GetString(10),
                DownloadedBytes = reader.GetInt64(11),
                TotalBytes = reader.IsDBNull(12) ? null : reader.GetInt64(12)
            };
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or IndexOutOfRangeException or OverflowException)
        {
            System.Diagnostics.Debug.WriteLine($"NovaClip skipped a malformed persisted task: {exception.Message}");
            return null;
        }
    }

    private async Task ExecuteWriteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void ValidateTableName(string table)
    {
        if (table is not ("DownloadTasks" or "DownloadHistory")) throw new ArgumentException("Unsupported persistence table.", nameof(table));
    }

    public void Dispose()
    {
        _writeGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
