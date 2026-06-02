using System.Security.Cryptography;
using System.Text;
using Erda.Configuration;
using Erda.Services;
using Microsoft.Extensions.Options;

namespace Erda.Scheduling;

/// <summary>Result of parsing the reminder note: valid reminders plus any rows that failed to parse.</summary>
public sealed record ReminderLoad(IReadOnlyList<Reminder> Reminders, IReadOnlyList<string> Malformed);

/// <summary>
/// Reads and writes the vault note that holds the reminder tables (one section per
/// <see cref="ReminderKind"/>). Definitions live here so Phil can hand-edit them in Obsidian; the
/// scheduler's run-state lives in a separate sidecar. Parsing is tolerant — a malformed row is
/// reported and skipped, never aborting the batch. Writes are serialized so the scheduler's
/// status updates and the tools' appends don't interleave.
/// </summary>
public sealed class ReminderStore(
    VaultService vault,
    IOptions<ReminderOptions> options,
    ILogger<ReminderStore> logger)
{
    private readonly object _lock = new();
    private string NotePath => options.Value.NotePath;

    private static readonly string[] Scaffold =
    [
        "# Erda Reminders",
        "",
        "Managed by Erda — edit, add, pause (status: paused), or delete rows here.",
        "Times are Europe/Berlin. `when` is a date-time (2026-06-15 09:00, fires once) or a cron",
        "expression (0 6 * * *, recurring; @daily/@weekly also work).",
        "",
        "## Reminders",
        "Sent to me verbatim at the scheduled time.",
        "",
        "| id | when | message | status |",
        "| --- | --- | --- | --- |",
        "",
        "## Scheduled prompts",
        "Run through Erda; the reply is sent to me.",
        "",
        "| id | when | prompt | status |",
        "| --- | --- | --- | --- |",
    ];

    public ReminderLoad LoadAll()
    {
        lock (_lock)
        {
            var lines = ReadLines();
            var reminders = new List<Reminder>();
            var malformed = new List<string>();
            foreach (var row in EnumerateRows(lines))
            {
                if (TryParseRow(row.Kind, row.Cells, out var reminder))
                    reminders.Add(reminder!);
                else
                    malformed.Add(lines[row.Index].Trim());
            }
            if (malformed.Count > 0)
                logger.LogWarning("Reminder note has {Count} malformed row(s); skipped.", malformed.Count);
            return new ReminderLoad(reminders, malformed);
        }
    }

    public void Append(ReminderKind kind, string id, string when, string text)
    {
        lock (_lock)
        {
            var lines = ReadLines();
            if (lines.Count == 0)
                lines.AddRange(Scaffold);
            InsertRow(lines, kind, RenderRow(id, when, text, ReminderStatus.Active));
            WriteLines(lines);
        }
    }

    public bool SetStatus(string id, ReminderStatus status)
    {
        lock (_lock)
        {
            var lines = ReadLines();
            foreach (var row in EnumerateRows(lines))
            {
                if (TryParseRow(row.Kind, row.Cells, out var r) && r!.Id == id)
                {
                    lines[row.Index] = RenderRow(r.Id, r.When, r.Text, status);
                    WriteLines(lines);
                    return true;
                }
            }
            return false;
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var lines = ReadLines();
            foreach (var row in EnumerateRows(lines))
            {
                if (TryParseRow(row.Kind, row.Cells, out var r) && r!.Id == id)
                {
                    lines.RemoveAt(row.Index);
                    WriteLines(lines);
                    return true;
                }
            }
            return false;
        }
    }

    // --- parsing -----------------------------------------------------------

    private readonly record struct RowRef(int Index, ReminderKind Kind, string[] Cells);

    /// <summary>Yield every data row (skipping headers/separators) with its section kind.</summary>
    private static IEnumerable<RowRef> EnumerateRows(IReadOnlyList<string> lines)
    {
        ReminderKind? kind = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("##"))
            {
                kind = SectionKind(trimmed);
                continue;
            }
            if (kind is null || !trimmed.StartsWith('|'))
                continue;
            var cells = SplitCells(trimmed);
            if (IsSeparatorRow(cells) || IsHeaderRow(cells))
                continue;
            yield return new RowRef(i, kind.Value, cells);
        }
    }

    private static ReminderKind? SectionKind(string headerLine)
    {
        var name = headerLine.TrimStart('#').Trim().ToLowerInvariant();
        return name switch
        {
            "reminders" => ReminderKind.Reminder,
            "scheduled prompts" => ReminderKind.Prompt,
            _ => null,
        };
    }

    private static bool TryParseRow(ReminderKind kind, string[] cells, out Reminder? reminder)
    {
        reminder = null;
        if (cells.Length < 3)
            return false;
        var id = cells[0];
        var when = cells[1];
        var text = cells[2];
        if (string.IsNullOrWhiteSpace(when) || string.IsNullOrWhiteSpace(text))
            return false;
        if (!WhenSpec.TryParse(when, out var spec))
            return false;
        if (string.IsNullOrWhiteSpace(id))
            id = DeriveId(kind, when, text);
        var status = ParseStatus(cells.Length > 3 ? cells[3] : "active");
        reminder = new Reminder(id, kind, when, text, status, spec!);
        return true;
    }

    private static ReminderStatus ParseStatus(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "done" => ReminderStatus.Done,
        "paused" => ReminderStatus.Paused,
        _ => ReminderStatus.Active,
    };

    /// <summary>Deterministic id for a row with a blank id cell (stable across processes).</summary>
    private static string DeriveId(ReminderKind kind, string when, string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{when}|{text}"));
        return "h" + Convert.ToHexString(hash)[..6].ToLowerInvariant();
    }

    /// <summary>Split a table row into trimmed cells, honoring an escaped pipe (<c>\|</c>) in text.</summary>
    private static string[] SplitCells(string trimmedLine)
    {
        var t = trimmedLine;
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];

        var cells = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '|')
            {
                sb.Append('|');
                i++;
            }
            else if (t[i] == '|')
            {
                cells.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(t[i]);
            }
        }
        cells.Add(sb.ToString().Trim());
        return cells.ToArray();
    }

    private static bool IsSeparatorRow(string[] cells) =>
        cells.Length > 0 && cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':'));

    private static bool IsHeaderRow(string[] cells) =>
        cells.Length > 0 && cells[0].Equals("id", StringComparison.OrdinalIgnoreCase);

    // --- writing -----------------------------------------------------------

    private static string RenderRow(string id, string when, string text, ReminderStatus status) =>
        $"| {id} | {when} | {text.Replace("|", "\\|")} | {status.ToString().ToLowerInvariant()} |";

    /// <summary>Insert a data row into the table of the given section, creating the section if absent.</summary>
    private static void InsertRow(List<string> lines, ReminderKind kind, string rowLine)
    {
        var (headerText, columnHeader) = kind == ReminderKind.Reminder
            ? ("## Reminders", "| id | when | message | status |")
            : ("## Scheduled prompts", "| id | when | prompt | status |");

        var secIdx = lines.FindIndex(l => l.Trim().StartsWith("##") && SectionKind(l.Trim()) == kind);
        if (secIdx < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                lines.Add("");
            lines.Add(headerText);
            lines.Add("");
            lines.Add(columnHeader);
            lines.Add("| --- | --- | --- | --- |");
            lines.Add(rowLine);
            return;
        }

        var sectionEnd = lines.Count;
        for (var i = secIdx + 1; i < lines.Count; i++)
        {
            if (lines[i].Trim().StartsWith("##"))
            {
                sectionEnd = i;
                break;
            }
        }

        var lastTable = -1;
        for (var i = secIdx + 1; i < sectionEnd; i++)
        {
            if (lines[i].Trim().StartsWith('|'))
                lastTable = i;
        }

        if (lastTable >= 0)
        {
            lines.Insert(lastTable + 1, rowLine);
        }
        else
        {
            lines.Insert(sectionEnd, columnHeader);
            lines.Insert(sectionEnd + 1, "| --- | --- | --- | --- |");
            lines.Insert(sectionEnd + 2, rowLine);
        }
    }

    private List<string> ReadLines()
    {
        try
        {
            return vault.ReadNote(NotePath).Replace("\r\n", "\n").Split('\n').ToList();
        }
        catch (FileNotFoundException)
        {
            return [];
        }
    }

    private void WriteLines(List<string> lines)
    {
        var content = string.Join("\n", lines).TrimEnd('\n') + "\n";
        vault.WriteNote(NotePath, content);
    }
}
