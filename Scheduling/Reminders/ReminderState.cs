using System.Text.Json;

namespace Erda.Scheduling;

/// <summary>
/// Machine-only run state for the reminder scheduler (kept out of the vault note). Drives recurring
/// cadence and guarantees a one-shot is sent at most once even if the note's status write fails.
/// </summary>
public sealed class ReminderState
{
    /// <summary>Per recurring reminder id, the last UTC instant it was fired.</summary>
    public Dictionary<string, DateTimeOffset> LastFiredUtc { get; set; } = new();

    /// <summary>One-shot ids already fired (the send-once authority); bounded by <see cref="Trim"/>.</summary>
    public List<string> FiredOneShotIds { get; set; } = [];

    /// <summary>Keep the fired-one-shot list from growing without bound (newest kept).</summary>
    public void Trim(int maxOneShots = 1000)
    {
        if (FiredOneShotIds.Count > maxOneShots)
            FiredOneShotIds.RemoveRange(0, FiredOneShotIds.Count - maxOneShots);
    }
}

/// <summary>Loads/saves <see cref="ReminderState"/> as a JSON file (best-effort), like the error-watch store.</summary>
public sealed class ReminderStateStore(string path, ILogger? logger = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Path => path;

    public ReminderState Load()
    {
        try
        {
            if (File.Exists(path))
            {
                var state = JsonSerializer.Deserialize<ReminderState>(File.ReadAllText(path));
                if (state is not null)
                    return state;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not load reminder state from {Path}; starting fresh.", path);
        }
        return new ReminderState();
    }

    public void Save(ReminderState state)
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
            logger?.LogWarning(ex, "Could not save reminder state to {Path}.", path);
        }
    }
}
