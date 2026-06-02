using Microsoft.EntityFrameworkCore;

namespace Erda.Core.Data;

/// <summary>
/// Versioned store for the orchestrator's system prompt. Every saved edit becomes a new
/// <see cref="PromptVersion"/>; exactly one version is active at a time, and prior versions are
/// retained so an edit can be rolled back. The first read seeds an active version from the
/// code-baked default so first run preserves current behaviour.
/// </summary>
public interface IPromptStore
{
    /// <summary>The active prompt's content. If no version exists yet, seed one (active) with
    /// <paramref name="codeDefault"/> and return it — so first run keeps current behaviour.</summary>
    string GetActiveContent(string codeDefault);

    /// <summary>All versions, newest first.</summary>
    IReadOnlyList<PromptVersion> ListVersions();

    /// <summary>Insert a new version and make it the active one (deactivating the prior active).
    /// Returns the new row.</summary>
    PromptVersion SaveNewVersion(string content, string? note);

    /// <summary>Make an existing version active (rollback). Returns false if the id is unknown.</summary>
    bool Activate(int versionId);
}

/// <summary>
/// EF Core-backed <see cref="IPromptStore"/>. Singleton-friendly: opens a short-lived
/// <see cref="ErdaDbContext"/> per operation via the injected
/// <see cref="IDbContextFactory{TContext}"/> rather than holding a shared context.
/// </summary>
public sealed class PromptStore(IDbContextFactory<ErdaDbContext> dbFactory) : IPromptStore
{
    /// <inheritdoc />
    public string GetActiveContent(string codeDefault)
    {
        using var db = dbFactory.CreateDbContext();

        // Guard against >1 active row by taking the newest (highest Id).
        var active = db.PromptVersions
            .Where(p => p.IsActive)
            .OrderByDescending(p => p.Id)
            .FirstOrDefault();

        if (active is not null)
            return active.Content;

        // No version exists yet — seed one from the code default so first run keeps behaviour.
        SaveNewVersion(codeDefault, "seeded from code default");
        return codeDefault;
    }

    /// <inheritdoc />
    public IReadOnlyList<PromptVersion> ListVersions()
    {
        using var db = dbFactory.CreateDbContext();
        return db.PromptVersions
            .OrderByDescending(p => p.Id)
            .ToList();
    }

    /// <inheritdoc />
    public PromptVersion SaveNewVersion(string content, string? note)
    {
        using var db = dbFactory.CreateDbContext();

        // Deactivate every currently-active row, add the new active row, persist in one SaveChanges.
        foreach (var existing in db.PromptVersions.Where(p => p.IsActive))
            existing.IsActive = false;

        var row = new PromptVersion
        {
            Content = content,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsActive = true,
            Note = note,
        };
        db.PromptVersions.Add(row);
        db.SaveChanges();
        return row;
    }

    /// <inheritdoc />
    public bool Activate(int versionId)
    {
        using var db = dbFactory.CreateDbContext();

        var target = db.PromptVersions.FirstOrDefault(p => p.Id == versionId);
        if (target is null)
            return false;

        foreach (var existing in db.PromptVersions.Where(p => p.IsActive))
            existing.IsActive = false;
        target.IsActive = true;

        db.SaveChanges();
        return true;
    }
}
