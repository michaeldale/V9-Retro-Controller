using Dapper;
using Microsoft.Data.Sqlite;

namespace V9Retro.Core.Data;

/// <summary>Opens connections to the SQLite file and applies the embedded migrations
/// (Data/Migrations/NNN_name.sql, tracked with PRAGMA user_version). WAL mode lets a future
/// service process read and write the same file while the app runs.</summary>
public sealed class Database
{
    static Database() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    readonly string _connectionString;

    public Database(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 10,
        }.ToString();
    }

    public string Path { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute("PRAGMA busy_timeout = 5000;");
        return connection;
    }

    public int Migrate()
    {
        using var connection = Open();
        connection.Execute("PRAGMA journal_mode = WAL;");
        var current = connection.ExecuteScalar<long>("PRAGMA user_version;");
        var applied = 0;
        foreach (var (number, sql) in LoadMigrations())
        {
            if (number <= current) continue;
            using var tx = connection.BeginTransaction();
            connection.Execute(sql, transaction: tx);
            connection.Execute($"PRAGMA user_version = {number};", transaction: tx);
            tx.Commit();
            applied++;
        }
        return applied;
    }

    static IEnumerable<(int Number, string Sql)> LoadMigrations()
    {
        var assembly = typeof(Database).Assembly;
        var list = new List<(int, string)>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) continue;
            var file = name[(name.LastIndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
            if (!int.TryParse(file.AsSpan(0, file.IndexOf('_')), out var number)) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.Add((number, reader.ReadToEnd()));
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }
}

public static class Clock
{
    /// <summary>Unix milliseconds; replaceable in tests.</summary>
    public static Func<long> Now { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static DateTimeOffset ToTime(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
}
