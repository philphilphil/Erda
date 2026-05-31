using System.Text.Json;

namespace Erda.Scheduling;

/// <summary>Persisted watermark + dedup memory for the error-watch scheduler.</summary>
public sealed class ErrorWatchState
{
    /// <summary>Newest event timestamp processed so far; the next poll queries from here.</summary>
    public DateTimeOffset? LastTimestampUtc { get; set; }

    /// <summary>Signatures already alerted on (bounded), so recurrences don't re-alert.</summary>
    public List<string> SeenSignatures { get; set; } = [];

    /// <summary>Recently processed event ids (bounded), to skip boundary duplicates across polls.</summary>
    public List<string> SeenEventIds { get; set; } = [];

    /// <summary>Keep the seen lists from growing without bound.</summary>
    public void Trim(int maxSignatures = 500, int maxEventIds = 500)
    {
        if (SeenSignatures.Count > maxSignatures)
            SeenSignatures.RemoveRange(0, SeenSignatures.Count - maxSignatures);
        if (SeenEventIds.Count > maxEventIds)
            SeenEventIds.RemoveRange(0, SeenEventIds.Count - maxEventIds);
    }
}

/// <summary>Loads/saves <see cref="ErrorWatchState"/> as a JSON file (best-effort).</summary>
public sealed class ErrorWatchStateStore(string path, ILogger? logger = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Path => path;

    public ErrorWatchState Load()
    {
        try
        {
            if (File.Exists(path))
            {
                var state = JsonSerializer.Deserialize<ErrorWatchState>(File.ReadAllText(path));
                if (state is not null)
                    return state;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not load error-watch state from {Path}; starting fresh.", path);
        }
        return new ErrorWatchState();
    }

    public void Save(ErrorWatchState state)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not save error-watch state to {Path}.", path);
        }
    }
}
