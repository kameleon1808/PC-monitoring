namespace Agent.Services;

public sealed record MetricsSnapshot
{
    public int? CpuPercent { get; init; }
    public float? CpuTempC { get; init; }
    public string? CpuTempSource { get; init; }
    public string? CpuTempStatus { get; init; }
    public string? CpuTempProvider { get; init; }
    public string? CpuTempHint { get; init; }
    public CpuTempDiagnostics? CpuTempDetails { get; init; }
    public float? GpuUsagePercent { get; init; }
    public float? GpuTempC { get; init; }
    public int? RamUsagePercent { get; init; }
    public int? RamUsedMb { get; init; }
    public int? RamTotalMb { get; init; }
    public int NetSendKbps { get; init; }
    public int NetReceiveKbps { get; init; }
    public MetricsSeries? Series { get; init; }
    public ProcessUsageSnapshot[] TopProcesses { get; init; } = Array.Empty<ProcessUsageSnapshot>();
    public string[] Errors { get; init; } = Array.Empty<string>();
}

public sealed record ProcessUsageSnapshot
{
    public string Name { get; init; } = string.Empty;
    public int? CpuPercent { get; init; }
    public int? RamPercent { get; init; }
    public int? GpuPercent { get; init; }
}

public sealed record CpuTempDiagnostics
{
    public bool IsAdmin { get; init; }
    public int CpuTempSensorsFound { get; init; }
    public int CpuTempSensorsWithValue { get; init; }
    public float? LastValidCpuTempC { get; init; }
    public int TicksSinceValid { get; init; }
    public int WarmupTicksRemaining { get; init; }
    public string? SelectedSensorName { get; init; }
    public string? SelectedSensorIdentifier { get; init; }
    public float? SelectedSensorValue { get; init; }
    public string? DerivedSensorName { get; init; }
    public float? DerivedSensorValue { get; init; }
    public float? DerivedSensorTjMax { get; init; }
    public string? Hint { get; init; }
}

public sealed record MetricsSeries
{
    public int[] NetSend60 { get; init; } = Array.Empty<int>();
    public int[] NetRecv60 { get; init; } = Array.Empty<int>();
}
