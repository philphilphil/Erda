using System.ComponentModel.DataAnnotations;

namespace Erda.Core.Configuration;

/// <summary>
/// Validation attribute for a <see cref="TimeSpan"/> setting that must be present and greater than
/// zero. A required TimeSpan has no sensible default — an unset one binds to <c>00:00:00</c>, which
/// this rejects so a missing interval/timeout stops the app at startup instead of, e.g., spinning a
/// scheduler at zero delay.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PositiveTimeSpanAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) => value is TimeSpan ts && ts > TimeSpan.Zero;

    public override string FormatErrorMessage(string name) =>
        $"{name} is required and must be a positive duration (e.g. 00:15:00).";
}
