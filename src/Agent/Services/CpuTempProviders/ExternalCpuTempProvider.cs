namespace Agent.Services.CpuTempProviders;

public sealed class ExternalCpuTempProvider : ICpuTempProvider
{
    private const string StatusValue = "external_not_configured";
    private const string ProviderValue = "external";
    private const string HintValue =
        "Configure external provider (e.g., HWiNFO shared memory) in a later phase.";

    public Task<CpuTempResult> GetAsync(CancellationToken ct)
    {
        return Task.FromResult(new CpuTempResult
        {
            TempC = null,
            Status = StatusValue,
            Hint = HintValue,
            Provider = ProviderValue
        });
    }
}
