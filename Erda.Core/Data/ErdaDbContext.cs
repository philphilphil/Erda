using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Erda.Core.Data;

/// <summary>
/// The single SQLite database for all of Erda's runtime/machine state: prompt versions, reminders
/// (definitions + run-state), error-watch state, the activity feed, and config overrides. Replaces
/// the old markdown reminder note + JSON sidecars. Consumers are singletons / background services,
/// so they take an <see cref="IDbContextFactory{TContext}"/> and create a short-lived context per
/// operation rather than sharing one instance.
/// </summary>
public sealed class ErdaDbContext(DbContextOptions<ErdaDbContext> options) : DbContext(options)
{
    public DbSet<PromptVersion> PromptVersions => Set<PromptVersion>();
    public DbSet<ReminderRow> Reminders => Set<ReminderRow>();
    public DbSet<ErrorWatchRow> ErrorWatchState => Set<ErrorWatchRow>();
    public DbSet<ActivityEntry> Activity => Set<ActivityEntry>();
    public DbSet<ConfigOverride> ConfigOverrides => Set<ConfigOverride>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<PromptVersion>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.IsActive);
        });
        b.Entity<ReminderRow>().HasKey(r => r.Id);
        b.Entity<ErrorWatchRow>().HasKey(e => e.Id);
        b.Entity<ActivityEntry>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.Id); // newest-first paging
        });
        b.Entity<ConfigOverride>().HasKey(c => c.Key);
    }
}

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can construct the context without the full
/// app host. Points at a throwaway local file; never used at runtime (the app supplies real
/// options via <c>AddDbContextFactory</c>).
/// </summary>
public sealed class ErdaDbContextFactory : IDesignTimeDbContextFactory<ErdaDbContext>
{
    public ErdaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ErdaDbContext>()
            .UseSqlite("Data Source=erda-design.db")
            .Options;
        return new ErdaDbContext(options);
    }
}
