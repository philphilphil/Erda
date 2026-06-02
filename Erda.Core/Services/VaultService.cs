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

    public void AppendNote(string path, string content)
    {
        var full = ResolveInside(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.AppendAllText(full, content);
    }
}
