using Xunit;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// The reverse read reports an undeclared sentence, proved by taking a declaration away.
///
/// <b>It passed on the first run it was written for, and that is the reason this test exists.</b> A
/// reconciliation whose first answer is "nothing is missing" is indistinguishable from one that
/// examines nothing, and this corpus has now found that shape in a check's scope, in a phase
/// report's claim register and in a runner job's inverted assertion. What has to be shown is that
/// the reverse read would say so if a claim went away.
///
/// Permanent rather than a break-and-revert done by hand once, on the rule that a test proving a
/// check works is part of the check.
/// </summary>
public sealed class SurfaceClaimsCheckProof
{
    [Fact]
    public void Removing_one_declaration_makes_its_own_sentence_undeclared()
    {
        IReadOnlyList<SurfaceClaimsCheck.Claim> all = SurfaceClaimsCheck.DeclaredClaims();

        // A claim whose sentence the reverse read actually matches. Chosen by asking rather than by
        // naming one: a test naming a claim that later stops being matched would prove nothing and
        // would keep passing, which is the failure it is here to rule out.
        SurfaceClaimsCheck.Claim? removable = all.FirstOrDefault(candidate =>
            SurfaceClaimsCheck.CorpusSentencesClaimingVisibility(
                [.. all.Where(c => c.Name != candidate.Name)]).Undeclared.Length > 0);

        Assert.True(removable is not null,
            "No declared claim, when removed, makes any corpus sentence undeclared. Either the reverse read "
            + "matches nothing the claim file covers, in which case the two are reconciling different sets, or "
            + "every matched sentence is covered by an exemption, in which case the exemptions are answering "
            + "the check.");

        (_, string[] undeclared) = SurfaceClaimsCheck.CorpusSentencesClaimingVisibility(
            [.. all.Where(c => c.Name != removable!.Name)]);

        // And the sentence it reports is that claim's own, rather than some other one that happened
        // to go undeclared for an unrelated reason.
        Assert.Contains(
            undeclared,
            reported => reported.Contains(removable!.Sentence, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_pattern_still_matches_the_corpus_it_was_calibrated_against()
    {
        (int examined, _) = SurfaceClaimsCheck.CorpusSentencesClaimingVisibility(
            SurfaceClaimsCheck.DeclaredClaims());

        // A sweep expecting a non-zero count states that count in advance. Sixteen when the pattern
        // was tightened at 4.1, from a looser one that matched thirty-four of which half were prose.
        // A lower bound rather than an equality, because the corpus grows.
        Assert.True(examined >= 16,
            $"The reverse read matched {examined} sentence(s), fewer than the 16 it matched when the pattern "
            + "was calibrated. A pattern that stopped matching would report nothing missing and read as green.");
    }

    [Fact]
    public void With_the_corpus_claims_declared_nothing_is_reported()
    {
        (_, string[] undeclared) = SurfaceClaimsCheck.CorpusSentencesClaimingVisibility(
            SurfaceClaimsCheck.DeclaredClaims());

        Assert.True(undeclared.Length == 0,
            "Corpus sentences claiming visibility that no claim declares: " + string.Join(" | ", undeclared));
    }
}
