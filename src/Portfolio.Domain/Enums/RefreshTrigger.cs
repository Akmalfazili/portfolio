namespace Portfolio.Domain.Enums;

/// <summary>
/// How a <see cref="Entities.RefreshRun"/> was initiated.
/// </summary>
public enum RefreshTrigger
{
    Scheduled = 0,
    Manual = 1,
}
