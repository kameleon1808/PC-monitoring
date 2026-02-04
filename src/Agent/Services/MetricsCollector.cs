using System.Diagnostics;
using System.Management;
using System.Threading;
using Agent.Services.CpuTempProviders;

namespace Agent.Services;

public sealed class MetricsCollector : BackgroundService
{
    private const int SeriesLength = 60;
    private const string CpuTempProviderLhm = "lhm";
    private const string CpuTempProviderWmi = "wmi";
    private const string CpuTempProviderExternal = "external";
    private readonly ILogger<MetricsCollector> _logger;
    private readonly HardwareMonitorService _hardwareMonitor;
    private readonly RuntimeStats _runtimeStats;
    private readonly LhmCpuTempProvider _lhmCpuTempProvider;
    private readonly WmiThermalZoneCpuTempProvider _wmiCpuTempProvider;
    private readonly ExternalCpuTempProvider _externalCpuTempProvider;
    private readonly string _cpuTempProvider;
    private readonly TimeSpan _baseInterval;
    private readonly TimeSpan _noClientInterval;
    private readonly bool _adaptiveNoClientInterval;
    private readonly bool _processMetricsEnabled;
    private readonly TimeSpan _processPollInterval;
    private readonly TimeSpan _gpuProcessPollInterval;
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _netSentCounter;
    private PerformanceCounter? _netReceivedCounter;
    private PerformanceCounter? _availableMemoryCounter;
    private readonly Dictionary<int, ProcessSample> _processSamples = new();
    private DateTimeOffset _lastProcessRead = DateTimeOffset.MinValue;
    private DateTimeOffset _lastGpuUsageRead = DateTimeOffset.MinValue;
    private ProcessUsageSnapshot[] _cachedTopProcesses = Array.Empty<ProcessUsageSnapshot>();
    private readonly Dictionary<int, float> _cachedGpuUsage = new();
    private readonly int[] _netSendSeries = new int[SeriesLength];
    private readonly int[] _netRecvSeries = new int[SeriesLength];
    private readonly object _seriesLock = new();
    private int _seriesIndex;
    private bool _seriesFilled;
    private readonly object _snapshotLock = new();
    private readonly MetricsSnapshotState _latestState = new();
    private readonly int? _totalMemoryMb;
    private readonly string? _totalMemoryError;

    public MetricsCollector(ILogger<MetricsCollector> logger, HardwareMonitorService hardwareMonitor, RuntimeStats runtimeStats,
        LhmCpuTempProvider lhmCpuTempProvider, WmiThermalZoneCpuTempProvider wmiCpuTempProvider,
        ExternalCpuTempProvider externalCpuTempProvider, MonitorSettings settings)
    {
        _logger = logger;
        _hardwareMonitor = hardwareMonitor;
        _runtimeStats = runtimeStats;
        _lhmCpuTempProvider = lhmCpuTempProvider;
        _wmiCpuTempProvider = wmiCpuTempProvider;
        _externalCpuTempProvider = externalCpuTempProvider;
        _cpuTempProvider = settings.CpuTempProvider;
        _baseInterval = TimeSpan.FromMilliseconds(settings.MetricsIntervalMs);
        _noClientInterval = TimeSpan.FromMilliseconds(settings.MetricsIntervalNoClientsMs);
        _adaptiveNoClientInterval = settings.AdaptiveUpdateNoClients;
        _processMetricsEnabled = settings.EnableProcessMetrics;
        _processPollInterval = TimeSpan.FromMilliseconds(settings.ProcessMetricsIntervalMs);
        _gpuProcessPollInterval = TimeSpan.FromMilliseconds(settings.GpuProcessIntervalMs);
        var totalMemorySnapshot = SystemMemoryInfo.GetTotalMemorySnapshot();
        _totalMemoryMb = totalMemorySnapshot.TotalMb;
        _totalMemoryError = totalMemorySnapshot.Error;
    }

    public MetricsSnapshot GetLatestSnapshot(bool includeSeries = false)
    {
        var series = includeSeries ? BuildSeriesSnapshot() : null;
        lock (_snapshotLock)
        {
            return new MetricsSnapshot
            {
                CpuPercent = _latestState.CpuPercent,
                CpuTempC = _latestState.CpuTempC,
                CpuTempSource = _latestState.CpuTempSource,
                CpuTempStatus = _latestState.CpuTempStatus,
                CpuTempProvider = _latestState.CpuTempProvider,
                CpuTempHint = _latestState.CpuTempHint,
                CpuTempDetails = _latestState.CpuTempDetails,
                GpuUsagePercent = _latestState.GpuUsagePercent,
                GpuTempC = _latestState.GpuTempC,
                RamUsagePercent = _latestState.RamUsagePercent,
                RamUsedMb = _latestState.RamUsedMb,
                RamTotalMb = _latestState.RamTotalMb,
                NetSendKbps = _latestState.NetSendKbps,
                NetReceiveKbps = _latestState.NetReceiveKbps,
                Series = series,
                TopProcesses = _latestState.TopProcesses,
                Errors = _latestState.Errors
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var errors = new List<string>();
        InitializeCpuCounter(errors);
        InitializeMemoryCounter(errors);
        await InitializeNetworkCountersAsync(errors, stoppingToken);
        var initStopwatch = Stopwatch.StartNew();
        var hardwareMetrics = _hardwareMonitor.GetLatestMetrics();
        var cpuTempResult = await ReadCpuTempResultAsync(errors, stoppingToken);
        int? ramUsagePercent;
        int? ramUsedMb;
        int? ramTotalMb;
        ReadRamMetrics(errors, out ramUsagePercent, out ramUsedMb, out ramTotalMb);
        var topProcesses = GetTopProcesses(ramTotalMb, errors);
        UpdateSnapshot(ReadCpuPercent(errors), ReadNetKbps(_netSentCounter, "Bytes Sent/sec", errors),
            ReadNetKbps(_netReceivedCounter, "Bytes Received/sec", errors), ramUsagePercent, ramUsedMb, ramTotalMb,
            topProcesses, hardwareMetrics, cpuTempResult, errors);
        initStopwatch.Stop();
        _runtimeStats.RecordTick(DateTimeOffset.UtcNow, initStopwatch.Elapsed.TotalMilliseconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                errors.Clear();
                var stopwatch = Stopwatch.StartNew();
                var cpuPercent = ReadCpuPercent(errors);
                var netSendKbps = ReadNetKbps(_netSentCounter, "Bytes Sent/sec", errors);
                var netReceiveKbps = ReadNetKbps(_netReceivedCounter, "Bytes Received/sec", errors);
                var hardwareSnapshot = _hardwareMonitor.GetLatestMetrics();
                var cpuTempSnapshot = await ReadCpuTempResultAsync(errors, stoppingToken);
                ReadRamMetrics(errors, out ramUsagePercent, out ramUsedMb, out ramTotalMb);
                topProcesses = GetTopProcesses(ramTotalMb, errors);
                UpdateSnapshot(cpuPercent, netSendKbps, netReceiveKbps, ramUsagePercent, ramUsedMb, ramTotalMb,
                    topProcesses, hardwareSnapshot, cpuTempSnapshot, errors);
                stopwatch.Stop();
                _runtimeStats.RecordTick(DateTimeOffset.UtcNow, stopwatch.Elapsed.TotalMilliseconds);
                await Task.Delay(GetCurrentInterval(), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown.
        }
    }

    public override void Dispose()
    {
        _cpuCounter?.Dispose();
        _netSentCounter?.Dispose();
        _netReceivedCounter?.Dispose();
        _availableMemoryCounter?.Dispose();
        base.Dispose();
    }

    private void InitializeCpuCounter(List<string> errors)
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            _cpuCounter.NextValue();
        }
        catch (Exception ex)
        {
            errors.Add($"CPU counter unavailable: {ex.Message}");
            _logger.LogWarning(ex, "CPU counter unavailable");
            _cpuCounter = null;
        }
    }

    private void InitializeMemoryCounter(List<string> errors)
    {
        try
        {
            _availableMemoryCounter = new PerformanceCounter("Memory", "Available MBytes", true);
            _availableMemoryCounter.NextValue();
        }
        catch (Exception ex)
        {
            errors.Add($"RAM counter unavailable: {ex.Message}");
            _logger.LogWarning(ex, "RAM counter unavailable");
            _availableMemoryCounter = null;
        }
    }

    private async Task InitializeNetworkCountersAsync(List<string> errors, CancellationToken token)
    {
        try
        {
            var counters = await SelectActiveNetworkInterfaceAsync(errors, token);
            _netSentCounter = counters?.Sent;
            _netReceivedCounter = counters?.Received;
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown.
        }
        catch (Exception ex)
        {
            errors.Add($"Network counters unavailable: {ex.Message}");
            _logger.LogWarning(ex, "Network counters unavailable");
            _netSentCounter = null;
            _netReceivedCounter = null;
        }
    }

    private async Task<NetworkCounters?> SelectActiveNetworkInterfaceAsync(List<string> errors, CancellationToken token)
    {
        var category = new PerformanceCounterCategory("Network Interface");
        var instances = category.GetInstanceNames();
        if (instances.Length == 0)
        {
            errors.Add("No network interfaces found.");
            return null;
        }

        var candidates = new List<NetworkCounters>(instances.Length);
        foreach (var instance in instances)
        {
            try
            {
                var sent = new PerformanceCounter("Network Interface", "Bytes Sent/sec", instance, true);
                var received = new PerformanceCounter("Network Interface", "Bytes Received/sec", instance, true);
                candidates.Add(new NetworkCounters(instance, sent, received));
            }
            catch (Exception ex)
            {
                errors.Add($"Network counter init failed ({instance}): {ex.Message}");
            }
        }

        if (candidates.Count == 0)
        {
            errors.Add("No network interface counters could be created.");
            return null;
        }

        foreach (var candidate in candidates)
        {
            candidate.Sent.NextValue();
            candidate.Received.NextValue();
        }

        await Task.Delay(TimeSpan.FromSeconds(2), token);

        NetworkCounters? best = null;
        float bestTotal = 0;
        var hadActive = false;

        foreach (var candidate in candidates)
        {
            var total = candidate.Sent.NextValue() + candidate.Received.NextValue();
            if (total > 0)
            {
                hadActive = true;
                if (best == null || total > bestTotal)
                {
                    best = candidate;
                    bestTotal = total;
                }
            }
        }

        if (best == null)
        {
            best = candidates[0];
            if (!hadActive)
            {
                errors.Add("No active network interface detected; using first available.");
            }
        }

        foreach (var candidate in candidates)
        {
            if (!ReferenceEquals(candidate, best))
            {
                candidate.Dispose();
            }
        }

        return best;
    }

    private int? ReadCpuPercent(List<string> errors)
    {
        if (_cpuCounter == null)
        {
            errors.Add("CPU counter not available.");
            return null;
        }

        try
        {
            var value = _cpuCounter.NextValue();
            return (int)Math.Round(value);
        }
        catch (Exception ex)
        {
            errors.Add($"CPU read failed: {ex.Message}");
            _logger.LogDebug(ex, "CPU read failed");
            return null;
        }
    }

    private int ReadNetKbps(PerformanceCounter? counter, string label, List<string> errors)
    {
        if (counter == null)
        {
            errors.Add($"{label} counter not available.");
            return 0;
        }

        try
        {
            var value = counter.NextValue();
            return ToKbps(value);
        }
        catch (Exception ex)
        {
            errors.Add($"{label} read failed: {ex.Message}");
            _logger.LogDebug(ex, "Network read failed for {Label}", label);
            return 0;
        }
    }

    private static int ToKbps(float bytesPerSecond)
    {
        var kbps = (bytesPerSecond * 8d) / 1000d;
        if (double.IsNaN(kbps) || double.IsInfinity(kbps))
        {
            return 0;
        }

        return (int)Math.Round(Math.Max(0, kbps));
    }

    private void ReadRamMetrics(List<string> errors, out int? ramUsagePercent, out int? ramUsedMb,
        out int? ramTotalMb)
    {
        ramUsagePercent = null;
        ramUsedMb = null;
        ramTotalMb = _totalMemoryMb;

        if (!_totalMemoryMb.HasValue || _totalMemoryMb.Value <= 0)
        {
            errors.Add(_totalMemoryError ?? "Total RAM unavailable.");
            ramTotalMb = null;
            return;
        }

        var availableMb = ReadAvailableMemoryMb(errors);
        if (!availableMb.HasValue)
        {
            return;
        }

        var usedMb = _totalMemoryMb.Value - availableMb.Value;
        if (usedMb < 0)
        {
            usedMb = 0;
        }

        var usagePercent = (double)usedMb / _totalMemoryMb.Value * 100d;
        var roundedPercent = (int)Math.Round(usagePercent);
        ramUsagePercent = Math.Clamp(roundedPercent, 0, 100);
        ramUsedMb = usedMb;
    }

    private int? ReadAvailableMemoryMb(List<string> errors)
    {
        if (_availableMemoryCounter == null)
        {
            errors.Add("RAM counter not available.");
            return null;
        }

        try
        {
            var value = _availableMemoryCounter.NextValue();
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                errors.Add("RAM read returned invalid value.");
                return null;
            }

            return (int)Math.Round(Math.Max(0, value));
        }
        catch (Exception ex)
        {
            errors.Add($"RAM read failed: {ex.Message}");
            _logger.LogDebug(ex, "RAM read failed");
            return null;
        }
    }

    private ProcessUsageSnapshot[] GetTopProcesses(int? totalMemoryMb, List<string> errors)
    {
        if (!_processMetricsEnabled)
        {
            return Array.Empty<ProcessUsageSnapshot>();
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastProcessRead < _processPollInterval)
        {
            return _cachedTopProcesses;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            errors.Add($"Process list unavailable: {ex.Message}");
            _cachedTopProcesses = Array.Empty<ProcessUsageSnapshot>();
            _lastProcessRead = now;
            return _cachedTopProcesses;
        }

        var cpuCount = Environment.ProcessorCount;
        var gpuUsage = GetGpuProcessUsage(errors, now);
        var entries = new List<ProcessUsageSnapshot>(5);
        var seen = new HashSet<int>();

        foreach (var process in processes)
        {
            int pid;
            string name;
            TimeSpan? totalCpu = null;
            long? workingSet = null;
            try
            {
                pid = process.Id;
                name = process.ProcessName;
                try
                {
                    totalCpu = process.TotalProcessorTime;
                }
                catch
                {
                }

                try
                {
                    workingSet = process.WorkingSet64;
                }
                catch
                {
                }
            }
            catch
            {
                continue;
            }
            finally
            {
                process.Dispose();
            }

            seen.Add(pid);
            int? cpuPercent = null;
            if (totalCpu.HasValue && _processSamples.TryGetValue(pid, out var sample))
            {
                var elapsedMs = (now - sample.Timestamp).TotalMilliseconds;
                var cpuMs = (totalCpu.Value - sample.TotalProcessorTime).TotalMilliseconds;
                if (elapsedMs > 0 && cpuMs >= 0)
                {
                    var percent = cpuMs / (elapsedMs * cpuCount) * 100d;
                    cpuPercent = (int)Math.Round(Math.Clamp(percent, 0d, 100d));
                }
            }

            if (totalCpu.HasValue)
            {
                _processSamples[pid] = new ProcessSample(totalCpu.Value, now);
            }

            int? ramPercent = null;
            if (workingSet.HasValue && totalMemoryMb.HasValue && totalMemoryMb.Value > 0)
            {
                var usedMb = workingSet.Value / (1024d * 1024d);
                var percent = usedMb / totalMemoryMb.Value * 100d;
                ramPercent = (int)Math.Round(Math.Clamp(percent, 0d, 100d));
            }

            int? gpuPercent = null;
            if (gpuUsage.TryGetValue(pid, out var gpuValue))
            {
                gpuPercent = (int)Math.Round(Math.Clamp(gpuValue, 0d, 100d));
            }

            var snapshot = new ProcessUsageSnapshot
            {
                Name = name,
                CpuPercent = cpuPercent,
                RamPercent = ramPercent,
                GpuPercent = gpuPercent
            };
            InsertTopProcess(entries, snapshot);
        }

        if (_processSamples.Count > seen.Count)
        {
            var stale = _processSamples.Keys.Where(pid => !seen.Contains(pid)).ToList();
            foreach (var pid in stale)
            {
                _processSamples.Remove(pid);
            }
        }

        entries.Sort(CompareProcessSnapshots);
        _cachedTopProcesses = entries.ToArray();
        _lastProcessRead = now;
        return _cachedTopProcesses;
    }

    private Dictionary<int, float> GetGpuProcessUsage(List<string> errors, DateTimeOffset now)
    {
        if (now - _lastGpuUsageRead < _gpuProcessPollInterval && _cachedGpuUsage.Count > 0)
        {
            return _cachedGpuUsage;
        }

        _cachedGpuUsage.Clear();
        ReadGpuProcessUsage(_cachedGpuUsage, errors);
        _lastGpuUsageRead = now;
        return _cachedGpuUsage;
    }

    private static void ReadGpuProcessUsage(Dictionary<int, float> usage, List<string> errors)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            using var results = searcher.Get();
            foreach (var entry in results)
            {
                using var obj = entry as ManagementObject;
                if (obj == null)
                {
                    continue;
                }

                var name = obj["Name"] as string;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!TryParseGpuPid(name, out var pid))
                {
                    continue;
                }

                if (!TryReadUsagePercent(obj["UtilizationPercentage"], out var percent))
                {
                    continue;
                }

                if (float.IsNaN(percent) || float.IsInfinity(percent))
                {
                    continue;
                }

                if (percent <= 0f)
                {
                    continue;
                }

                if (usage.TryGetValue(pid, out var existing))
                {
                    usage[pid] = existing + percent;
                }
                else
                {
                    usage[pid] = percent;
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"GPU process counters unavailable: {ex.Message}");
        }
    }

    private static void InsertTopProcess(List<ProcessUsageSnapshot> entries, ProcessUsageSnapshot snapshot)
    {
        entries.Add(snapshot);
        entries.Sort(CompareProcessSnapshots);
        if (entries.Count > 5)
        {
            entries.RemoveAt(entries.Count - 1);
        }
    }

    private static int CompareProcessSnapshots(ProcessUsageSnapshot left, ProcessUsageSnapshot right)
    {
        var gpuCompare = (right.GpuPercent ?? 0).CompareTo(left.GpuPercent ?? 0);
        if (gpuCompare != 0)
        {
            return gpuCompare;
        }

        var cpuCompare = (right.CpuPercent ?? 0).CompareTo(left.CpuPercent ?? 0);
        if (cpuCompare != 0)
        {
            return cpuCompare;
        }

        var ramCompare = (right.RamPercent ?? 0).CompareTo(left.RamPercent ?? 0);
        if (ramCompare != 0)
        {
            return ramCompare;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
    }

    private static bool TryParseGpuPid(string name, out int pid)
    {
        pid = 0;
        var start = name.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        start += 4;
        var end = start;
        while (end < name.Length && char.IsDigit(name[end]))
        {
            end++;
        }

        if (end == start)
        {
            return false;
        }

        return int.TryParse(name[start..end], out pid);
    }

    private static bool TryReadUsagePercent(object? raw, out float percent)
    {
        percent = 0f;
        if (raw == null)
        {
            return false;
        }

        switch (raw)
        {
            case uint value:
                percent = value;
                return true;
            case ulong value:
                percent = value;
                return true;
            case int value:
                percent = value;
                return true;
            case long value:
                percent = value;
                return true;
            case float value:
                percent = value;
                return true;
            case double value:
                percent = (float)value;
                return true;
            case string text when float.TryParse(text, out var parsed):
                percent = parsed;
                return true;
            default:
                return false;
        }
    }

    private TimeSpan GetCurrentInterval()
    {
        if (_adaptiveNoClientInterval && _runtimeStats.WebSocketClients == 0)
        {
            return _noClientInterval;
        }

        return _baseInterval;
    }

    private async Task<CpuTempResult> ReadCpuTempResultAsync(List<string> errors, CancellationToken token)
    {
        CpuTempResult result;
        try
        {
            result = await SelectCpuTempProvider().GetAsync(token);
        }
        catch (Exception ex)
        {
            errors.Add($"CPU temp provider failed: {ex.Message}");
            result = new CpuTempResult
            {
                TempC = null,
                Status = "no_values",
                Hint = null,
                Provider = _cpuTempProvider
            };
        }

        return result;
    }

    private ICpuTempProvider SelectCpuTempProvider()
    {
        return _cpuTempProvider switch
        {
            CpuTempProviderWmi => _wmiCpuTempProvider,
            CpuTempProviderExternal => _externalCpuTempProvider,
            _ => _lhmCpuTempProvider
        };
    }

    private void UpdateSnapshot(int? cpuPercent, int netSendKbps, int netReceiveKbps, int? ramUsagePercent,
        int? ramUsedMb, int? ramTotalMb, ProcessUsageSnapshot[] topProcesses, HardwareMetrics hardwareMetrics,
        CpuTempResult cpuTemp, List<string> errors)
    {
        AppendSeriesData(netSendKbps, netReceiveKbps);
        var errorSnapshot = errors.Count == 0 ? Array.Empty<string>() : errors.ToArray();
        lock (_snapshotLock)
        {
            _latestState.CpuPercent = cpuPercent;
            _latestState.CpuTempC = cpuTemp.TempC;
            _latestState.CpuTempProvider = cpuTemp.Provider;
            _latestState.CpuTempStatus = cpuTemp.Status;
            _latestState.CpuTempHint = cpuTemp.Hint;
            _latestState.CpuTempSource = cpuTemp.Provider == CpuTempProviderLhm
                ? hardwareMetrics.CpuTempSource
                : null;
            _latestState.CpuTempDetails = cpuTemp.Provider == CpuTempProviderLhm
                ? hardwareMetrics.CpuTempDetails
                : null;
            _latestState.GpuUsagePercent = hardwareMetrics.GpuUsagePercent;
            _latestState.GpuTempC = hardwareMetrics.GpuTempC;
            _latestState.RamUsagePercent = ramUsagePercent;
            _latestState.RamUsedMb = ramUsedMb;
            _latestState.RamTotalMb = ramTotalMb;
            _latestState.NetSendKbps = netSendKbps;
            _latestState.NetReceiveKbps = netReceiveKbps;
            _latestState.TopProcesses = topProcesses;
            _latestState.Errors = errorSnapshot;
        }
    }

    private void AppendSeriesData(int netSendKbps, int netReceiveKbps)
    {
        lock (_seriesLock)
        {
            _netSendSeries[_seriesIndex] = netSendKbps;
            _netRecvSeries[_seriesIndex] = netReceiveKbps;
            _seriesIndex++;
            if (_seriesIndex >= SeriesLength)
            {
                _seriesIndex = 0;
                _seriesFilled = true;
            }
        }
    }

    private MetricsSeries BuildSeriesSnapshot()
    {
        var send = new int[SeriesLength];
        var recv = new int[SeriesLength];

        lock (_seriesLock)
        {
            if (!_seriesFilled)
            {
                var tailCount = _seriesIndex;
                var padding = SeriesLength - tailCount;
                if (tailCount > 0)
                {
                    Array.Copy(_netSendSeries, 0, send, padding, tailCount);
                    Array.Copy(_netRecvSeries, 0, recv, padding, tailCount);
                }
            }
            else
            {
                var headCount = SeriesLength - _seriesIndex;
                Array.Copy(_netSendSeries, _seriesIndex, send, 0, headCount);
                Array.Copy(_netRecvSeries, _seriesIndex, recv, 0, headCount);
                if (_seriesIndex > 0)
                {
                    Array.Copy(_netSendSeries, 0, send, headCount, _seriesIndex);
                    Array.Copy(_netRecvSeries, 0, recv, headCount, _seriesIndex);
                }
            }
        }

        return new MetricsSeries
        {
            NetSend60 = send,
            NetRecv60 = recv
        };
    }

    private readonly record struct ProcessSample(TimeSpan TotalProcessorTime, DateTimeOffset Timestamp);

    private sealed class NetworkCounters : IDisposable
    {
        public NetworkCounters(string name, PerformanceCounter sent, PerformanceCounter received)
        {
            Name = name;
            Sent = sent;
            Received = received;
        }

        public string Name { get; }
        public PerformanceCounter Sent { get; }
        public PerformanceCounter Received { get; }

        public void Dispose()
        {
            Sent.Dispose();
            Received.Dispose();
        }
    }

    private sealed class MetricsSnapshotState
    {
        public int? CpuPercent;
        public float? CpuTempC;
        public string? CpuTempSource;
        public string? CpuTempStatus;
        public string? CpuTempProvider;
        public string? CpuTempHint;
        public CpuTempDiagnostics? CpuTempDetails;
        public float? GpuUsagePercent;
        public float? GpuTempC;
        public int? RamUsagePercent;
        public int? RamUsedMb;
        public int? RamTotalMb;
        public int NetSendKbps;
        public int NetReceiveKbps;
        public ProcessUsageSnapshot[] TopProcesses = Array.Empty<ProcessUsageSnapshot>();
        public string[] Errors = Array.Empty<string>();
    }
}
