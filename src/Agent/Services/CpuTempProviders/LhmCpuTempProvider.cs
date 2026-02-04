using Agent.Services;

namespace Agent.Services.CpuTempProviders;

public sealed class LhmCpuTempProvider : ICpuTempProvider
{
    private readonly HardwareMonitorService _monitor;

    public LhmCpuTempProvider(HardwareMonitorService monitor)
    {
        _monitor = monitor;
    }

    public Task<CpuTempResult> GetAsync(CancellationToken ct)
    {
        return Task.FromResult(_monitor.GetLatestCpuTempResult());
    }
}
