namespace StorageGO;

/// <summary>Per-pool limits. Each API process and each pool has its own limits.</summary>
public sealed class SmbClientPoolOptions
{
    public int MaxClients { get; init; } = 8;
    public int MaxPendingAcquires { get; init; } = 32;
    public TimeSpan AcquireTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxClients);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxPendingAcquires);
        if (AcquireTimeout <= TimeSpan.Zero || AcquireTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(AcquireTimeout));
        if (IdleTimeout <= TimeSpan.Zero || IdleTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(IdleTimeout));
    }
}

/// <summary>Thrown without starting SMB I/O when the bounded acquisition queue is full.</summary>
public sealed class SmbClientPoolFullException : InvalidOperationException
{
    public SmbClientPoolFullException() : base("The SMB client pool acquisition queue is full.") { }
}

/// <summary>A point-in-time snapshot; idle clients exclude unused, uninitialized pool slots.</summary>
public sealed record SmbClientPoolStatistics(
    int ActiveLeases, int PendingAcquires, int IdleClients,
    long ClientsCreated, long ClientsDiscarded, long AcquireTimeouts,
    long RejectedAcquires, long CleanupFailures);
