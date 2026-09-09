namespace Graphify.CSharp.Incremental;

internal sealed record IncrementalWatcherOptions
{
    public IncrementalWatcherOptions(
        TimeSpan? backupScanInterval = null,
        TimeSpan? recoveryRetryDelay = null)
    {
        BackupScanInterval = ValidatePositive(
            backupScanInterval ?? TimeSpan.FromMinutes(5),
            nameof(backupScanInterval));
        RecoveryRetryDelay = ValidatePositive(
            recoveryRetryDelay ?? TimeSpan.FromSeconds(1),
            nameof(recoveryRetryDelay));
    }

    public TimeSpan BackupScanInterval { get; }

    public TimeSpan RecoveryRetryDelay { get; }

    private static TimeSpan ValidatePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The interval must be positive.");
        }

        return value;
    }
}
