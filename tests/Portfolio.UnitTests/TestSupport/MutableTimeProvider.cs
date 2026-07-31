namespace Portfolio.UnitTests.TestSupport;

/// <summary>A <see cref="TimeProvider"/> whose reported instant can be advanced mid-test — used
/// where <see cref="FixedTimeProvider"/>'s single fixed instant isn't enough, e.g. proving a
/// refresh source becomes due again only after its interval has actually elapsed.</summary>
internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
