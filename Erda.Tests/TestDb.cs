using Erda.Data;
using Microsoft.EntityFrameworkCore;

namespace Erda.Tests;

/// <summary>
/// Creates a throwaway, file-backed SQLite <see cref="ErdaDbContext"/> factory for tests. Each call
/// gets its own temp database with the schema created. Use one factory per test so cases stay
/// isolated; share a single factory across collaborators (store + state store) that must see the
/// same rows.
/// </summary>
public static class TestDb
{
    public static IDbContextFactory<ErdaDbContext> NewFactory()
    {
        var path = Path.Combine(Path.GetTempPath(), "erda-test-" + Guid.NewGuid().ToString("N") + ".db");
        var options = new DbContextOptionsBuilder<ErdaDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        var factory = new Factory(options);
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        return factory;
    }

    private sealed class Factory(DbContextOptions<ErdaDbContext> options) : IDbContextFactory<ErdaDbContext>
    {
        public ErdaDbContext CreateDbContext() => new(options);
    }
}
