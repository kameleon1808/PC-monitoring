namespace Agent.Services;

public sealed record MetricsSnapshot
{
    public int? CpuPercent { get; init; }
    public float? CpuTempC { get; init; }
    public string? CpuTempSource { get; init; }
    public float? GpuUsagePercent { get; init; }
    public float? GpuTempC { get; init; }
    public int? RamUsagePercent { get; init; }
    public int? RamUsedMb { get; init; }
    public int? RamTotalMb { get; init; }
    public int NetSendKbps { get; init; }
    public int NetReceiveKbps { get; init; }
    public MetricsSeries? Series { get; init; }
    public string[] Errors { get; init; } = Array.Empty<string>();
}

public sealed record MetricsSeries
{
    public int[] NetSend60 { get; init; } = Array.Empty<int>();
    public int[] NetRecv60 { get; init; } = Array.Empty<int>();
}
