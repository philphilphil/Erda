using System.Text;
using Erda.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Erda.Core.Services;

/// <summary>
/// Path-safe file IO confined to the configured Obsidian vault root.
/// Every public method that takes a path rejects anything that escapes the root.
/// </summary>
public sealed class VaultService
{
    /// <summary>Absolute, normalized vault root.</summary>
    public string Root { get; }

    public VaultService(IOptions<ErdaOptions> options)
    {
        Root = Path.GetFullPath(options.Value.VaultPath);
        Directory.CreateDirectory(Root);
    }

    /// <summary>Resolve a vault-relative path to an absolute path, throwing if it escapes the root.</summary>
    public string ResolveInside(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("A vault-relative path is required.", nameof(relativePath));

        var combined = Path.GetFullPath(Path.Combine(Root, relativePath));
        var rootPrefix = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        if (combined != Root && !combined.StartsWith(rootPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Path '{relativePath}' escapes the vault root.");
        return combined;
    }

    private string ToRelative(string absolutePath)
        => Path.GetRelativePath(Root, absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    // Skip dot-folders/files such as .obsidian, .trash, .git, .DS_Store.
    private bool IsHidden(string absolutePath)
        => ToRelative(absolutePath).Split('/').Any(seg => seg.StartsWith('.'));

    public IReadOnlyList<string> ListNotes(string? subfolder)
    {
        var dir = string.IsNullOrWhiteSpace(subfolder) ? Root : ResolveInside(subfolder);
        if (!Directory.Exists(dir))
            return Array.Empty<string>();

        return Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
            .Where(p => !IsHidden(p))
            .Select(ToRelative)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string ReadNote(string path)
    {
        var full = ResolveInside(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"Note not found: {path}");
        return File.ReadAllText(full);
    }

    /// <summary>True if a note exists at the vault-relative path (false if the path escapes the root).</summary>
    public bool Exists(string path)
    {
        try { return File.Exists(ResolveInside(path)); }
        catch { return false; }
    }

    public IReadOnlyList<(string Path, string Snippet)> Search(string query, int maxResults = 50)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<(string, string)>();

        var results = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(Root, "*.md", SearchOption.AllDirectories))
        {
            if (IsHidden(file))
                continue;

            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                continue;

            var start = Math.Max(0, idx - 40);
            var length = Math.Min(text.Length - start, query.Length + 80);
            var snippet = text.Substring(start, length).Replace('\n', ' ').Replace('\r', ' ').Trim();
            results.Add((ToRelative(file), snippet));

            if (results.Count >= maxResults)
                break;
        }
        return results;
    }

    public void WriteNote(string path, string content)
    {
        var full = ResolveInside(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>
    /// Renames or moves a note and rewrites every <c>[[wikilink]]</c> (and <c>![[embed]]</c>) in the
    /// vault that points to it, keeping any <c>#heading</c>, <c>|alias</c> and <c>.md</c> suffix.
    /// A link written as a bare name (<c>[[Note]]</c>) counts as pointing here only when no other
    /// note shares that name, which is how Obsidian resolves it too. After the move it is rewritten
    /// to the new bare name, or to the new path when that name would be ambiguous. A link written as a
    /// path (<c>[[Folder/Note]]</c>) is matched against the old path and rewritten to the new one.
    /// Markdown-style <c>[text](path.md)</c> links are not touched. Refuses to overwrite an existing
    /// note, except for a change of case alone. Path-safe on both ends.
    /// </summary>
    public MoveResult MoveNote(string fromPath, string toPath)
    {
        var fromFull = ResolveInside(fromPath);
        var toFull = ResolveInside(toPath);
        if (!fromFull.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !toFull.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Both paths must be Markdown notes ending in .md.");
        if (!File.Exists(fromFull))
            throw new FileNotFoundException($"Note not found: {fromPath}");
        if (string.Equals(fromFull, toFull, StringComparison.Ordinal))
            throw new InvalidOperationException("The source and destination are the same note.");

        var caseOnly = string.Equals(fromFull, toFull, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && File.Exists(toFull))
            throw new InvalidOperationException($"A note already exists at {toPath}; refusing to overwrite it.");

        var oldRel = ToRelative(fromFull);
        var newRel = ToRelative(toFull);
        var oldName = Path.GetFileNameWithoutExtension(fromFull);
        var newName = Path.GetFileNameWithoutExtension(toFull);

        // Bare-name resolution needs the other notes' names, taken before the move.
        var otherNames = ListNotes(null)
            .Where(p => !string.Equals(p, oldRel, StringComparison.Ordinal))
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToList();
        var oldNameUnique = !otherNames.Contains(oldName, StringComparer.OrdinalIgnoreCase);
        var newNameUnique = !otherNames.Contains(newName, StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(Path.GetDirectoryName(toFull)!);
        if (caseOnly)
        {
            // Case-insensitive filesystems treat a case-only rename as a no-op; go through a temp name.
            var temp = fromFull + ".erda-move";
            File.Move(fromFull, temp);
            File.Move(temp, toFull);
        }
        else
        {
            File.Move(fromFull, toFull);
        }

        var oldPathNoExt = oldRel[..^3];
        var newPathNoExt = newRel[..^3];
        var updated = new List<string>();
        var linkCount = 0;

        foreach (var file in Directory.EnumerateFiles(Root, "*.md", SearchOption.AllDirectories))
        {
            if (IsHidden(file))
                continue;

            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            var changed = 0;
            var rewritten = WikiLink.Replace(text, m =>
            {
                var target = m.Groups["target"].Value;
                var trimmed = target.Trim();
                var hasExt = trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
                var bare = hasExt ? trimmed[..^3] : trimmed;

                string? replacement = null;
                if (bare.Contains('/'))
                {
                    if (string.Equals(bare.TrimStart('/'), oldPathNoExt, StringComparison.OrdinalIgnoreCase))
                        replacement = newPathNoExt;
                }
                else if (oldNameUnique && string.Equals(bare, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    replacement = newNameUnique ? newName : newPathNoExt;
                }

                if (replacement is null)
                    return m.Value;

                changed++;
                return $"{m.Groups["embed"].Value}[[{replacement}{(hasExt ? ".md" : "")}{m.Groups["rest"].Value}]]";
            });

            if (changed == 0)
                continue;

            File.WriteAllText(file, rewritten);
            updated.Add(ToRelative(file));
            linkCount += changed;
        }

        return new MoveResult(oldRel, newRel, linkCount, updated);
    }

    /// <summary><c>[[target#heading|alias]]</c>, optionally an <c>!</c> embed; one line, no nesting.</summary>
    private static readonly System.Text.RegularExpressions.Regex WikiLink = new(
        @"(?<embed>!?)\[\[(?<target>[^\]\|#\n]+)(?<rest>(?:#[^\]\|\n]*)?(?:\|[^\]\n]*)?)\]\]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public void AppendNote(string path, string content)
    {
        var full = ResolveInside(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.AppendAllText(full, content);
    }

    /// <summary>
    /// Build the hierarchical editing conventions for a note's location. Walks from the vault root
    /// down through every ancestor folder of the note (including the note's own folder), collects each
    /// <c>AGENTS.md</c>, and concatenates them <b>root-first, nearest-last</b> — each chunk headed by
    /// its scope, the whole opening with a one-line precedence note. Missing <c>AGENTS.md</c> files and
    /// dot-folders (<c>.obsidian</c>/<c>.trash</c>) are skipped. Returns a minimal fallback when none
    /// exist. Path-safe: an escaping <paramref name="notePath"/> throws via <see cref="ResolveInside"/>.
    /// Reads only the convention files, never the note itself.
    /// </summary>
    public string StackConventions(string notePath)
    {
        var full = ResolveInside(notePath);
        var noteDir = Path.GetDirectoryName(full) ?? Root;

        // Chain of directories from the note's own folder up to the root, then reversed to root-first.
        var dirs = new List<string>();
        var current = noteDir;
        while (current is not null && current.Length >= Root.Length)
        {
            dirs.Add(current);
            if (string.Equals(current, Root, StringComparison.Ordinal))
                break;
            current = Path.GetDirectoryName(current);
        }
        dirs.Reverse();

        var chunks = new List<string>();
        foreach (var dir in dirs)
        {
            var isRoot = string.Equals(dir, Root, StringComparison.Ordinal);
            var rel = ToRelative(dir);

            // Skip dot-folders (reuse IsHidden); the root resolves to "." and is hidden, hence the guard.
            if (!isRoot && IsHidden(dir))
                continue;

            var agentsPath = Path.Combine(dir, "AGENTS.md");
            if (!File.Exists(agentsPath))
                continue;

            string body;
            try { body = File.ReadAllText(agentsPath); }
            catch { continue; }

            var scope = isRoot ? "(vault root)" : rel + "/";
            chunks.Add($"### Conventions: {scope}\n{body.TrimEnd()}");
        }

        if (chunks.Count == 0)
            return $"No vault editing conventions (AGENTS.md) apply to '{notePath}'. " +
                   "Apply standard Markdown note conventions and preserve the note's existing style.";

        var sb = new StringBuilder();
        sb.AppendLine("Vault editing conventions. Later sections are nearer the note and win on conflict.");
        sb.AppendLine();
        sb.Append(string.Join("\n\n", chunks));
        return sb.ToString();
    }

    /// <summary>
    /// Anchored, surgical edit: <paramref name="oldString"/> must occur in the note <b>exactly once</b>
    /// — 0 matches or &gt;1 matches throw a clear <see cref="InvalidOperationException"/>; on exactly one
    /// it is replaced with <paramref name="newString"/> and the note written back. This structurally
    /// enforces "never touch surrounding text". Path-safe via <see cref="ResolveInside"/>.
    /// </summary>
    public void ReplaceInNote(string path, string oldString, string newString)
    {
        if (string.IsNullOrEmpty(oldString))
            throw new ArgumentException("The anchor (oldString) must be non-empty.", nameof(oldString));

        var full = ResolveInside(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"Note not found: {path}");

        var text = File.ReadAllText(full);

        var count = 0;
        for (var i = text.IndexOf(oldString, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(oldString, i + oldString.Length, StringComparison.Ordinal))
            count++;

        if (count == 0)
            throw new InvalidOperationException(
                $"Anchor not found in {path}. The exact text to replace does not appear in the note.");
        if (count > 1)
            throw new InvalidOperationException(
                $"Anchor is not unique in {path} ({count} matches); add more surrounding context to target a single location.");

        File.WriteAllText(full, text.Replace(oldString, newString, StringComparison.Ordinal));
    }
}

/// <summary>The outcome of <see cref="VaultService.MoveNote"/>: where the note went and which notes had links rewritten.</summary>
public sealed record MoveResult(string From, string To, int LinksUpdated, IReadOnlyList<string> NotesUpdated);
