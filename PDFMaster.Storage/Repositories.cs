using Microsoft.Data.Sqlite;
using PDFMaster.Core;
using PDFMaster.Domain;

namespace PDFMaster.Storage;

public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly SqliteDatabase _db;

    public SqliteSettingsStore(SqliteDatabase db) => _db = db;

    public Task<IReadOnlyDictionary<string, string>> LoadAllAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) values[reader.GetString(0)] = reader.GetString(1);
        return (IReadOnlyDictionary<string, string>)values;
    }, ct);

    public Task SaveAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = _db.Open();
        using var tx = connection.BeginTransaction();
        foreach (var pair in values)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO settings (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$key", pair.Key);
            cmd.Parameters.AddWithValue("$value", pair.Value);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }, ct);
}

public sealed class SqliteHistoryRepository : IHistoryRepository
{
    private readonly SqliteDatabase _db;

    public SqliteHistoryRepository(SqliteDatabase db) => _db = db;

    public Task<long> AddAsync(HistoryRecord record, CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history (file_name, operation, started_utc, result, output_path, duration_ms, status)
            VALUES ($file, $op, $started, $result, $output, $duration, $status);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$file", record.FileName);
        cmd.Parameters.AddWithValue("$op", record.Operation);
        cmd.Parameters.AddWithValue("$started", SqliteDatabase.ToDbTime(record.StartedUtc));
        cmd.Parameters.AddWithValue("$result", record.Result);
        cmd.Parameters.AddWithValue("$output", (object?)record.OutputPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$duration", (long)record.Duration.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$status", (int)record.Status);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }, ct);

    public Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryQuery query, CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        var where = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            where.Add("(file_name LIKE $text ESCAPE '\\' OR operation LIKE $text ESCAPE '\\' OR result LIKE $text ESCAPE '\\')");
            cmd.Parameters.AddWithValue("$text", "%" + EscapeLike(query.Text.Trim()) + "%");
        }

        if (!string.IsNullOrWhiteSpace(query.Operation))
        {
            where.Add("operation = $op");
            cmd.Parameters.AddWithValue("$op", query.Operation);
        }

        if (query.FromUtc is { } from)
        {
            where.Add("started_utc >= $from");
            cmd.Parameters.AddWithValue("$from", SqliteDatabase.ToDbTime(from));
        }

        if (query.ToUtc is { } to)
        {
            where.Add("started_utc < $to");
            cmd.Parameters.AddWithValue("$to", SqliteDatabase.ToDbTime(to));
        }

        cmd.CommandText =
            "SELECT id, file_name, operation, started_utc, result, output_path, duration_ms, status FROM history" +
            (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty) +
            " ORDER BY started_utc DESC, id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 10_000));

        var list = new List<HistoryRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new HistoryRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                SqliteDatabase.FromDbTime(reader.GetString(3)),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                TimeSpan.FromMilliseconds(reader.GetInt64(6)),
                (OperationStatus)reader.GetInt32(7)));
        }

        return (IReadOnlyList<HistoryRecord>)list;
    }, ct);

    public Task<IReadOnlyList<string>> GetOperationNamesAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT operation FROM history ORDER BY operation;";
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return (IReadOnlyList<string>)list;
    }, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM history WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }, ct);

    public Task ClearAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM history;";
        cmd.ExecuteNonQuery();
    }, ct);

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

public sealed class SqliteRecentFilesRepository : IRecentFilesRepository
{
    private const int MaxEntries = 50;
    private readonly SqliteDatabase _db;

    public SqliteRecentFilesRepository(SqliteDatabase db) => _db = db;

    public Task TouchAsync(string path, DateTime openedUtc, CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = _db.Open();
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO recent_files (path, last_opened_utc) VALUES ($path, $time)
                ON CONFLICT(path) DO UPDATE SET last_opened_utc = excluded.last_opened_utc;
                """;
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$time", SqliteDatabase.ToDbTime(openedUtc));
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM recent_files WHERE path NOT IN (
                    SELECT path FROM recent_files ORDER BY last_opened_utc DESC LIMIT $max);
                """;
            cmd.Parameters.AddWithValue("$max", MaxEntries);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }, ct);

    public Task<IReadOnlyList<RecentFile>> GetAsync(int limit, CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT path, last_opened_utc FROM recent_files ORDER BY last_opened_utc DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxEntries));
        var list = new List<RecentFile>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RecentFile(reader.GetString(0), SqliteDatabase.FromDbTime(reader.GetString(1))));
        }

        return (IReadOnlyList<RecentFile>)list;
    }, ct);

    public Task RemoveAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM recent_files WHERE path = $path;";
        cmd.Parameters.AddWithValue("$path", path);
        cmd.ExecuteNonQuery();
    }, ct);

    public Task ClearAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM recent_files;";
        cmd.ExecuteNonQuery();
    }, ct);
}
