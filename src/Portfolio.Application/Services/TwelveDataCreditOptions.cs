namespace Portfolio.Application.Services;

/// <summary>
/// Overridable form of <see cref="TwelveDataCreditPolicy"/>'s constants, bound from the same
/// <c>"TwelveData"</c> configuration section <c>TwelveDataOptions</c> (in Portfolio.Infrastructure)
/// reads <c>ApiKey</c>/<c>BaseUrl</c> from — both classes bind independently from the same node, so
/// adding this never disturbs the existing secret-handling for <c>ApiKey</c>. Existing only so unit
/// tests can shrink the per-minute/daily limits instead of waiting on the real 60-second window.
/// </summary>
public sealed class TwelveDataCreditOptions
{
    public const string SectionName = "TwelveData";

    public int PerMinuteCreditLimit { get; set; } = TwelveDataCreditPolicy.PerMinuteCreditLimit;

    public int DailyCreditBudget { get; set; } = TwelveDataCreditPolicy.DailyCreditBudget;

    /// <summary>See <see cref="TwelveDataCreditPolicy.ReconciliationIntervalMinutes"/>. Overridable
    /// so unit tests can shrink it instead of waiting on a real hour.</summary>
    public int ReconciliationIntervalMinutes { get; set; } = TwelveDataCreditPolicy.ReconciliationIntervalMinutes;
}
