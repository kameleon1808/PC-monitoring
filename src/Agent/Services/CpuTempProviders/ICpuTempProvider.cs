namespace Agent.Services.CpuTempProviders;

public interface ICpuTempProvider
{
    Task<CpuTempResult> GetAsync(CancellationToken ct);
}

public sealed record CpuTempResult
{
    public float? TempC { get; init; }
    public string Status { get; init; } = "no_values";
    public string? Hint { get; init; }
    public string Provider { get; init; } = "lhm";
}
