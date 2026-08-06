namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// A clock the test moves by hand.
/// <para>
/// Anything that expires -- a snooze, a retention window, a sample old enough to stop being a
/// baseline -- can only be tested honestly by moving time, not by waiting for it.
/// </para>
/// </summary>
/// <param name="now">Where time starts.</param>
internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
