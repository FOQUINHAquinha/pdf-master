using Microsoft.Data.Sqlite;

namespace PDFMaster.Storage;

/// <summary>
/// Banco SQLite local. Guarda apenas configurações, histórico de operações e arquivos recentes;
/// nunca o conteúdo dos documentos.
/// </summary>
public sealed class SqliteDatabase
{
    private const int SchemaVersion = 2;
    private readonly string _connectionString;

    public SqliteDatabase(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
    }

    public string DatabasePath { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Cria ou atualiza o esquema. Seguro para chamar a cada inicialização.</summary>
    public void Initialize()
    {
        using var connection = Open();

        long version;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt64(cmd.ExecuteScalar());
        }

        if (version >= SchemaVersion) return;

        using var tx = connection.BeginTransaction();
        if (version < 1) CreateVersion1(connection, tx);
        if (version < 2)
        {
            // Versão 2: ferramenta e arquivos de entrada de cada operação ("Repetir operação").
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "ALTER TABLE history ADD COLUMN tool_id TEXT NULL; ALTER TABLE history ADD COLUMN inputs TEXT NULL;";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"PRAGMA user_version = {SchemaVersion};";
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void CreateVersion1(SqliteConnection connection, SqliteTransaction tx)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS settings (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS history (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_name   TEXT NOT NULL,
                    operation   TEXT NOT NULL,
                    started_utc TEXT NOT NULL,
                    result      TEXT NOT NULL,
                    output_path TEXT NULL,
                    duration_ms INTEGER NOT NULL,
                    status      INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_history_started ON history (started_utc DESC);
                CREATE TABLE IF NOT EXISTS recent_files (
                    path            TEXT PRIMARY KEY COLLATE NOCASE,
                    last_opened_utc TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
    }

    internal static string ToDbTime(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    internal static DateTime FromDbTime(string value) =>
        DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
}
