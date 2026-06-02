using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Erda.Configuration;

/// <summary>
/// Configuration source that layers a SQLite-backed override table on top of the rest of the
/// configuration stack. Register it last (after appsettings and environment variables) so its
/// values win.
/// <para>
/// The backing table is <c>ConfigOverrides(Key TEXT PRIMARY KEY, Value TEXT)</c>. Keys are stored
/// in standard ASP.NET <c>Section:Key</c> form (e.g. <c>"ErrorWatch:MinLevel"</c>), so they bind to
/// strongly-typed options exactly like any other configuration provider.
/// </para>
/// <para>
/// READ-ONCE: the table is read a single time at startup. There is no reloading and no change
/// tokens — editing the table at runtime has no effect until the app restarts. v1 deliberately
/// applies config changes on restart only.
/// </para>
/// </summary>
public sealed class SqliteConfigurationSource(string dbPath) : IConfigurationSource
{
    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new SqliteConfigurationProvider(dbPath);
}

/// <summary>
/// Reads override rows from the <c>ConfigOverrides</c> table of a SQLite database once, at startup,
/// into the configuration stack. See <see cref="SqliteConfigurationSource"/> for the layering and
/// read-once semantics (restart to apply changes).
/// <para>
/// Best-effort by design: a missing database file, a missing table, or any read failure leaves the
/// override set empty rather than throwing — overrides are optional, and the very first run happens
/// before the database exists. Built before the DI container, so this uses raw
/// <see cref="SqliteConnection"/> rather than EF Core.
/// </para>
/// </summary>
public sealed class SqliteConfigurationProvider(string dbPath) : ConfigurationProvider
{
    /// <summary>
    /// Loads every <c>Key</c>/<c>Value</c> row from <c>ConfigOverrides</c> into <see cref="ConfigurationProvider.Data"/>.
    /// Returns early (leaving <c>Data</c> empty) if the database file or table is absent, and swallows
    /// any exception so a broken override store never blocks startup. The inherited <c>Data</c>
    /// dictionary already uses a case-insensitive comparer, matching ASP.NET key semantics.
    /// </summary>
    public override void Load()
    {
        try
        {
            // First run before the override DB is created must not throw.
            if (!File.Exists(dbPath))
                return;

            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            // Defensively confirm the table exists before querying it.
            using (var tableCheck = conn.CreateCommand())
            {
                tableCheck.CommandText =
                    "SELECT name FROM sqlite_master WHERE type='table' AND name='ConfigOverrides';";
                if (tableCheck.ExecuteScalar() is null)
                    return;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Key, Value FROM ConfigOverrides;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var value = reader.IsDBNull(1) ? null : reader.GetString(1);
                Data[key] = value;
            }
        }
        catch
        {
            // Best-effort: config overrides are optional and there is no logger at this stage.
        }
    }
}

/// <summary>
/// Convenience registration for the SQLite override source.
/// </summary>
public static class SqliteConfigurationExtensions
{
    /// <summary>
    /// Adds the SQLite override source to the configuration builder. Call this last so its values
    /// override appsettings and environment variables. See <see cref="SqliteConfigurationSource"/>
    /// for the read-once / restart-to-apply behaviour.
    /// </summary>
    public static IConfigurationBuilder AddSqliteOverrides(this IConfigurationBuilder builder, string dbPath)
        => builder.Add(new SqliteConfigurationSource(dbPath));
}
