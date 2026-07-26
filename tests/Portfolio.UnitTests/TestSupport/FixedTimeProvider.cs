namespace Portfolio.UnitTests.TestSupport;

/// <summary>A <see cref="TimeProvider"/> that always reports a fixed instant, so "no future trade
/// dates" validation is deterministic in tests.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
