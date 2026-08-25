using System.Text.RegularExpressions;

namespace PullbackStrategyLab.Data;

/// <summary>
/// Table and pragma names cannot be parameterised, so the few places that compose one
/// into SQL validate it here first. Everything else is a parameter.
/// </summary>
public static partial class SqliteIdentifier
{
    [GeneratedRegex("^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Allowed();

    public static void Validate(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        if (!Allowed().IsMatch(identifier))
        {
            throw new ArgumentException(
                $"'{identifier}' is not a valid store identifier. Store identifiers are lowercase with underscores.",
                nameof(identifier));
        }
    }
}
