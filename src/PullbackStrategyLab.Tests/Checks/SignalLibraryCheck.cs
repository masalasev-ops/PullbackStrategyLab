using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// The signal library says the same thing in the specification, in the code that seeds it, and in
/// the store it is seeded into.
///
/// <b>Three statements of one library, reconciled in every direction.</b> SCHEMA.md's Signals
/// section is the specification. <see cref="SignalLibrary"/> is the runnable copy the Worker ships
/// with, because the application has no access to `docs/` at runtime and a store has to stay a
/// directory that can be copied. `signal_definition` is what SignalAdmissionTest writes from that
/// copy. Two of the three could agree while the third drifts, so all three are compared rather than
/// a chain of two.
/// see: The signal library stays a spec section and gains a runtime table, reconciled in both directions
///
/// <b>Exactly one disagreement is admitted and it is named.</b> A stored row may read
/// `rejected_correlation` where the section reads `candidate`, because a rejection is something the
/// admission test measured rather than something the specification declares. Anything else fails: a
/// signal in one place and not another, a formula that drifted, a source-column list that lost an
/// entry, a status that moved without a verdict behind it.
///
/// <b>Why this is not left to a test in the vectorizer's file.</b> One already asserts that the
/// active set and what the vectorizer freezes partition each other, and it is the reason the
/// section could be trusted through phase 2. What it cannot see is a second statement of the
/// library, which did not exist until 6.2 and now does, and the corpus's own rule is that a
/// property holding at every moment is a check rather than something a test happens to cover.
/// </summary>
public sealed partial class SignalLibraryCheck : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 6, 22, 0, 0, TimeSpan.Zero));

    public SignalLibraryCheck(ITestOutputHelper output)
    {
        _output = output;
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose()
    {
        _root.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Markup removed so a cell and a code string can be compared as the sentences they are.
    ///
    /// <b>Markup-tolerant over the span it matches, which is a rule this corpus learned the hard
    /// way.</b> A formula is written with emphasis wherever the writer wanted emphasis and with
    /// code ticks around every identifier, and an escaped pipe stands for the absolute-value bars in
    /// two of the trade-geometry rows. Comparing the raw cell against a plain string would fail on
    /// every row that carries any of the three, and the obvious repair, comparing only the letters,
    /// would stop seeing a formula that changed its operators.
    /// </summary>
    public static string Normalise(string cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return Whitespace().Replace(
            cell.Replace("\\|", "|", StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal)
                .Replace("**", string.Empty, StringComparison.Ordinal),
            " ").Trim();
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [Fact]
    [Trait("check", "signal-library")]
    public void The_specification_the_code_and_the_store_declare_one_library()
    {
        var coverage = new CheckCoverage("signal-library", _output);

        IReadOnlyList<SignalDeclaration> section = SchemaSignals.All;
        IReadOnlyDictionary<string, DeclaredSignal> code = SignalLibrary.ByName;

        var problems = new List<string>();

        // Both directions between the document and the code. A signal in one and not the other is
        // the failure this check is named for, and it is the one a session adding a signal to a
        // single place makes.
        foreach (string missing in section.Select(s => s.Name).Except(code.Keys, StringComparer.Ordinal))
        {
            problems.Add(
                $"{missing} is declared in SCHEMA.md's Signals section and is not in SignalLibrary.Declared, "
                + "so nothing seeds it into the store and no proposal can reach it.");
        }

        foreach (string extra in code.Keys.Except(section.Select(s => s.Name), StringComparer.Ordinal))
        {
            problems.Add(
                $"{extra} is in SignalLibrary.Declared and is not declared in SCHEMA.md's Signals section, "
                + "so it would be seeded into the library with no formula anybody wrote down.");
        }

        int compared = 0;

        foreach (SignalDeclaration declared in section)
        {
            if (!code.TryGetValue(declared.Name, out DeclaredSignal? mirror))
            {
                continue;
            }

            compared++;

            if (Normalise(declared.Formula) != mirror.Formula)
            {
                problems.Add(
                    $"{declared.Name}: the section's formula is \"{Normalise(declared.Formula)}\" and the code's is "
                    + $"\"{mirror.Formula}\".");
            }

            if (Normalise(declared.SourceColumns) != mirror.SourceColumns)
            {
                problems.Add(
                    $"{declared.Name}: the section's source columns are \"{Normalise(declared.SourceColumns)}\" and the "
                    + $"code's are \"{mirror.SourceColumns}\".");
            }

            if (Normalise(declared.Status) != mirror.Status)
            {
                problems.Add(
                    $"{declared.Name}: the section's status is \"{Normalise(declared.Status)}\" and the code's is "
                    + $"\"{mirror.Status}\".");
            }
        }

        // The section may state two statuses and never the third, which is the asymmetry the whole
        // reconciliation rests on. A cell that stated a rejection would be the document claiming a
        // measurement.
        foreach (SignalDeclaration declared in section.Where(s => !SignalStatus.Declarable.Contains(Normalise(s.Status))))
        {
            problems.Add(
                $"{declared.Name}: the section states \"{Normalise(declared.Status)}\", and a section may state "
                + $"{string.Join(" or ", SignalStatus.Declarable)} only. A rejection is measured by "
                + "SignalAdmissionTest rather than declared, so it belongs on the stored row and not in the cell.");
        }

        // The planted null is a fact about one signal and it is asserted against the section's own
        // prose rather than against the code that names it, because the code naming itself would be
        // the assertion reading its own subject.
        string signals = Normalise(SectionText());

        if (!signals.Contains($"{SignalLibrary.NullControl} is the planted null control", StringComparison.Ordinal))
        {
            problems.Add(
                $"SCHEMA.md's Signals section does not say that {SignalLibrary.NullControl} is the planted null "
                + "control, and it is the one signal whose stored row carries is_null_control.");
        }

        foreach (DeclaredSignal control in SignalLibrary.Declared.Where(s => s.IsNullControl))
        {
            if (control.Status != SignalStatus.Candidate)
            {
                problems.Add(
                    $"{control.Name} is the planted null control and is declared {control.Status}. A signal that "
                    + "means nothing is never frozen on a setup; it is planted in the conditional tables at 6.4.");
            }
        }

        // From 7.4, a sourced candidate cites the SOURCES.md clause it was derived from, in the code
        // and in the section's own clause cell, and the clause is a heading SOURCES.md carries. Every
        // row of the sourced table cites one, and no signal outside it does, so a candidate cannot be
        // moved into the sourced set without saying which clause it came from.
        string sources = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "SOURCES.md"));
        IReadOnlyDictionary<string, string> cells = ClauseCells();
        int clausesResolved = 0;

        foreach (DeclaredSignal signal in SignalLibrary.Declared)
        {
            cells.TryGetValue(signal.Name, out string? cell);

            if (signal.Clause is null && cell is null)
            {
                continue;
            }

            if (signal.Clause != cell)
            {
                problems.Add($"{signal.Name}: the code cites clause \"{signal.Clause}\" and the section's clause cell "
                    + $"reads \"{cell}\".");
                continue;
            }

            if (signal.Status != SignalStatus.Candidate)
            {
                problems.Add($"{signal.Name}: a sourced signal is declared {signal.Status}. It is frozen so it can be "
                    + "replayed and it is a candidate until the admission test rules on it.");
            }

            if (!sources.Contains($"### {signal.Clause}\n", StringComparison.Ordinal)
                && !sources.Contains($"### {signal.Clause}\r\n", StringComparison.Ordinal))
            {
                problems.Add($"{signal.Name}: cites \"{signal.Clause}\", which is not a clause heading in SOURCES.md, "
                    + "so the form it claims to rest on cannot be found.");
                continue;
            }

            clausesResolved++;
        }

        // The third statement, and the only one that is a behaviour rather than a list: what the
        // stage actually writes. A seed that dropped a row, wrote a formula it had made up, or lost
        // the null-control flag would satisfy everything above.
        IReadOnlyList<StoredDefinition> stored = SeedAndRead();

        foreach (string missing in code.Keys.Except(stored.Select(s => s.Name), StringComparer.Ordinal))
        {
            problems.Add($"{missing} is declared in code and SignalAdmissionTest did not seed it into signal_definition.");
        }

        foreach (string extra in stored.Select(s => s.Name).Except(code.Keys, StringComparer.Ordinal))
        {
            problems.Add($"{extra} is in signal_definition and is declared nowhere, so the seed invented a row.");
        }

        foreach (StoredDefinition row in stored)
        {
            if (!code.TryGetValue(row.Name, out DeclaredSignal? mirror))
            {
                continue;
            }

            if (row.Formula != mirror.Formula || row.SourceColumns != mirror.SourceColumns)
            {
                problems.Add($"{row.Name}: the stored formula or source columns differ from the declared ones.");
            }

            if (row.IsNullControl != mirror.IsNullControl)
            {
                problems.Add($"{row.Name}: the stored null-control flag is {row.IsNullControl} and the declared one is {mirror.IsNullControl}.");
            }

            // The one admitted difference, stated as an allowance rather than left to pass by
            // accident: a rejection may sit where the section says candidate, and nothing else may.
            bool admitted = row.Status == mirror.Status
                || (row.Status == SignalStatus.RejectedCorrelation && mirror.Status == SignalStatus.Candidate);

            if (!admitted)
            {
                problems.Add(
                    $"{row.Name}: the stored status is \"{row.Status}\" and the declared one is \"{mirror.Status}\". "
                    + "The only difference a run may produce is a rejection where the section says candidate.");
            }
        }

        coverage
            .Examined("signals reconciled between SCHEMA.md and SignalLibrary, in both directions", compared)
            .Examined("signals reconciled between SignalLibrary and the seeded signal_definition", stored.Count)
            .Examined("status cells checked against the two a specification may state", section.Count)
            .Examined("sourced candidates whose clause resolves to a SOURCES.md heading", clausesResolved)
            .NoSourceScan(
                "every side of this check is the thing itself rather than a description of one. The section is "
                + "the specification, SignalLibrary is the list the Worker ships, and the third side is a store "
                + "the check seeds by running the shipped stage, so the behaviour is exercised rather than read "
                + "out of the source.")
            .Report();

        // Stated in advance. An empty section, an empty library or an empty seed satisfies every
        // reconciliation above by having nothing to disagree about, which is the shape this corpus
        // has shipped four times.
        Assert.True(compared >= 35,
            $"only {compared} signal(s) were reconciled between the section and the code, and the library held "
            + "forty-one when this was written, so one of the two sides stopped being read.");
        Assert.True(stored.Count >= 35,
            $"the seed wrote {stored.Count} row(s) into signal_definition, and the library declares {code.Count}, "
            + "so the stage wrote almost nothing and every comparison against it passed by being empty.");

        Assert.True(problems.Count == 0,
            $"{problems.Count} disagreement(s) between the three statements of the signal library:\n  "
            + string.Join("\n  ", problems));
    }

    /// <summary>The fifth cell of every five-column signal row, which is the sourced table's clause.</summary>
    [GeneratedRegex(
        @"^\|\s*`(?<name>[a-z_0-9]+)`\s*\|(?:(?:\\\||[^|])*\|){3}\s*(?<clause>[a-z][a-z-]*, (?:long|short))\s*\|\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ClauseCell();

    private static IReadOnlyDictionary<string, string> ClauseCells() =>
        ClauseCell().Matches(SectionText()).ToDictionary(
            m => m.Groups["name"].Value, m => m.Groups["clause"].Value, StringComparer.Ordinal);

    /// <summary>SCHEMA.md's Signals section as text, bounded by its own heading like every read of it.</summary>
    private static string SectionText()
    {
        string schema = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "SCHEMA.md"));
        int start = schema.IndexOf(SchemaSignals.Heading, StringComparison.Ordinal);
        int end = schema.IndexOf("## Trading", start, StringComparison.Ordinal);
        return end < 0 ? schema[start..] : schema[start..end];
    }

    /// <summary>The library as the shipped stage seeds it into an empty store.</summary>
    private IReadOnlyList<StoredDefinition> SeedAndRead()
    {
        IOptions<PullbackStrategyLabOptions> options =
            Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

        new SignalAdmissionTest(_connections, new RunLogger(_clock, options), _clock, options)
            .Admit(new DateOnly(2026, 9, 6));

        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT signal_name, formula, source_columns, status, is_null_control FROM signal_definition";

        var rows = new List<StoredDefinition>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new StoredDefinition(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4) == 1));
        }

        return rows;
    }

    private sealed record StoredDefinition(
        string Name, string Formula, string SourceColumns, string Status, bool IsNullControl);
}
