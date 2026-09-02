namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// What holding a short costs, at the rate the position recorded when it opened.
///
/// <b>Pure, and it charges the rate the row carries rather than the constant.</b>
/// <see cref="BorrowAssumption.AnnualisedRate"/> is what a new position stamps on itself; this takes
/// the rate as an argument, so a trade closed today is charged what its own position assumed rather
/// than what the constant says now. That is the fault <c>trade_plan</c> stores <c>equity</c> and
/// <c>risk_fraction</c> to avoid, arriving one table later.
///
/// <b>Calendar days, and a year of 365 of them.</b> Borrow accrues on days held rather than on
/// sessions, so a Friday-to-Monday hold costs three days and not one. The year is the calendar's
/// because a calendar day is a 365th of one and nothing further has to be argued; the 360-day year
/// the market actually uses is a convention that would need a defence, and at 1.0% a year the choice
/// moves a four-day hold by about a thousandth of a per cent of position value.
/// see: Long and short are never pooled into one figure
///
/// <b>Nothing is charged on a long and nothing is charged on a same-day short.</b> A position closed
/// in the session it opened in was never held overnight, and overnight is when borrow accrues.
/// </summary>
public static class BorrowCost
{
    /// <summary>Days in the year the rate is annualised over.</summary>
    public const int DaysInTheYear = 365;

    /// <summary>
    /// The money a short holding <paramref name="valueAtEntry"/> costs over
    /// <paramref name="calendarDaysHeld"/> at <paramref name="annualisedRate"/>.
    ///
    /// Unrounded. The figure is money and money is decimal here, and rounding it to the cent would
    /// put every four-day hold of a small position at nought, which reads as a cost that was not
    /// charged rather than as one too small to see.
    /// </summary>
    public static decimal Charged(decimal valueAtEntry, decimal annualisedRate, int calendarDaysHeld)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueAtEntry);
        ArgumentOutOfRangeException.ThrowIfNegative(annualisedRate);
        ArgumentOutOfRangeException.ThrowIfNegative(calendarDaysHeld);

        return valueAtEntry * annualisedRate * calendarDaysHeld / DaysInTheYear;
    }
}
