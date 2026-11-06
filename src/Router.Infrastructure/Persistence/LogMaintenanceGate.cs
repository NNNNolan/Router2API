namespace Router.Infrastructure.Persistence;

public sealed class LogMaintenanceGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}
