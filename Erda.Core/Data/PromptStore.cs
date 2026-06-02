using Microsoft.EntityFrameworkCore;

namespace Erda.Core.Data;

/// <summary>
/// Versioned store for the agent's prompts, keyed by <see cref="PromptKind"/>. Every saved edit
/// becomes a new <see cref="PromptVersion"/>; exactly one version per kind is active at a time, and
/// prior versions are retained so an edit can be rolled back. The first read of a kind seeds an
/// active version from the code-baked default so first run preserves current behaviour.
/// </summary>
public interface IPromptStore
{
    /// <summary>The active content for <paramref name="kind"/>. If no version exists yet, seed one
    /// (active) with <paramref name="codeDefault"/> and return it — so first run keeps current
    /// behaviour.</summary>
    string GetActiveContent(string kind, string codeDefault);

    /// <summary>All versions of <paramref name="kind"/>, newest first.</summary>
    IReadOnlyList<PromptVersion> ListVersions(string kind);

    /// <summary>Insert a new version of <paramref name="kind"/> and make it the active one
    /// (deactivating that kind's prior active). Returns the new row.</summary>
    PromptVersion SaveNewVersion(string kind, string content, string? note);

    /// <summary>Make an existing version active (rollback) — deactivating the prior active row of the
    /// same kind. Returns false if the id is unknown.</summary>
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
    public string GetActiveContent(string kind, string codeDefault)
    {
        using var db = dbFactory.CreateDbContext();

        // Guard against >1 active row by taking the newest (highest Id).
        var active = db.PromptVersions
            .Where(p => p.Kind == kind && p.IsActive)
            .OrderByDescending(p => p.Id)
            .FirstOrDefault();

        if (active is not null)
            return active.Content;

        // No version exists yet for this kind — seed one from the code default so first run keeps behaviour.
        SaveNewVersion(kind, codeDefault, "seeded from code default");
        return codeDefault;
    }

    /// <inheritdoc />
    public IReadOnlyList<PromptVersion> ListVersions(string kind)
    {
        using var db = dbFactory.CreateDbContext();
        return db.PromptVersions
            .Where(p => p.Kind == kind)
            .OrderByDescending(p => p.Id)
            .ToList();
    }

    /// <inheritdoc />
    public PromptVersion SaveNewVersion(string kind, string content, string? note)
    {
        using var db = dbFactory.CreateDbContext();

        // Deactivate this kind's currently-active rows, add the new active row, persist in one SaveChanges.
        foreach (var existing in db.PromptVersions.Where(p => p.Kind == kind && p.IsActive))
            existing.IsActive = false;

        var row = new PromptVersion
        {
            Kind = kind,
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

        // Only touch the active flag within the target's own kind.
        foreach (var existing in db.PromptVersions.Where(p => p.Kind == target.Kind && p.IsActive))
            existing.IsActive = false;
        target.IsActive = true;

        db.SaveChanges();
        return true;
    }
}
