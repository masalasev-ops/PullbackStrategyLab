using PullbackStrategyLab.Core.Time;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The five scheduled tasks against the thirty-eight slots they run, from 7.17.
///
/// <b>Each rule is one a wrong window would break without anything else noticing.</b> A slot left
/// out of every window is a slot no task fires, which is the fault 6.7 found in five slots declared
/// everywhere and registered nowhere. A slot in two windows runs twice. A window out of declaration
/// order runs a slot before the one whose rows it reads. A spread pass sharing a window with anything
/// fires at the wrong minute of the session, and a book sampled at the wrong minute has no later
/// chance.
/// </summary>
public sealed class NightWindowTests
{
    [Fact]
    public void Every_slot_runs_in_exactly_one_window_and_no_window_names_a_slot_that_is_not_declared()
    {
        string[] declared = [.. NightlySchedule.Slots.Select(s => s.Slot)];
        string[] windowed = [.. NightlySchedule.Windows.SelectMany(w => w.Slots)];

        Assert.Equal(declared.Order(StringComparer.Ordinal), windowed.Order(StringComparer.Ordinal));
        Assert.Equal(windowed.Length, windowed.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Contiguous over the slots that fire on the same days, because the weekly slots were appended
    /// before <c>snapshot</c> in the declaration and fire on a different day, so they cannot run
    /// between two weekday slots whatever order the list happens to hold them in.
    /// </summary>
    [Fact]
    public void A_window_runs_its_slots_in_declaration_order_as_one_unbroken_run_of_its_own_days()
    {
        foreach (NightWindow window in NightlySchedule.Windows)
        {
            string[] sameDays = [.. NightlySchedule.Slots.Where(s => s.WeeklyOn == window.WeeklyOn).Select(s => s.Slot)];
            int first = Array.IndexOf(sameDays, window.Slots[0]);

            Assert.True(first >= 0, $"window {window.Window} opens on {window.Slots[0]}, which does not fire on its days");
            Assert.Equal(window.Slots, sameDays.Skip(first).Take(window.Slots.Count));
        }
    }

    [Fact]
    public void A_window_fires_at_its_first_slots_time_and_on_its_slots_days()
    {
        foreach (NightWindow window in NightlySchedule.Windows)
        {
            NightSlot[] slots = [.. window.Slots.Select(name => NightlySchedule.Slots.Single(s => s.Slot == name))];

            Assert.Equal(slots[0].At, window.At);
            Assert.All(slots, s => Assert.Equal(window.WeeklyOn, s.WeeklyOn));
        }
    }

    [Fact]
    public void A_spread_pass_is_alone_in_its_window_because_it_has_to_fire_at_its_own_minute_of_the_session()
    {
        foreach (NightSlot pass in NightlySchedule.Slots.Where(s => s.InsideTheSession))
        {
            NightWindow? window = NightlySchedule.WindowOf(pass.Slot);

            Assert.NotNull(window);
            Assert.Equal([pass.Slot], window.Slots);
            Assert.Equal(pass.At, window.At);
        }
    }
}
