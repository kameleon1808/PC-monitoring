using LibreHardwareMonitor.Hardware;
using System.Management;
using System.Threading;

namespace Agent.Services;

public sealed class HardwareMonitorService : BackgroundService
{
    private const int CpuTempInvalidThreshold = 5;
    private readonly TimeSpan _pollInterval;
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CpuFallbackInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<HardwareMonitorService> _logger;
    private readonly Computer _computer;
    private DateTimeOffset _lastScanAttempt = DateTimeOffset.MinValue;
    private bool _computerOpen;

    private readonly List<IHardware> _cpuHardware = new();
    private readonly List<IHardware> _gpuHardware = new();
    private readonly List<IHardware> _motherboardHardware = new();
    private readonly List<IHardware> _hardwareToUpdate = new();
    private ISensor? _cpuTempSensor;
    private string? _cpuTempSource;
    private ISensor? _gpuTempSensor;
    private ISensor? _gpuUsageSensor;
    private int _cpuTempInvalidTicks;
    private DateTimeOffset _lastCpuFallbackRead = DateTimeOffset.MinValue;
    private float? _cachedCpuFallbackTemp;
    private string? _cachedCpuFallbackSource;

    private volatile HardwareMetrics _latest = new()
    {
        CpuTempC = null,
        CpuTempSource = null,
        GpuUsagePercent = null,
        GpuTempC = null
    };

    public HardwareMonitorService(ILogger<HardwareMonitorService> logger, MonitorSettings settings)
    {
        _logger = logger;
        _pollInterval = TimeSpan.FromMilliseconds(settings.HardwareIntervalMs);
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true
        };
        TryOpenComputer();
    }

    public HardwareMetrics GetLatestMetrics()
    {
        return Volatile.Read(ref _latest);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ScanSensorsIfNeeded(force: true);

        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Tick();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown.
        }
    }

    public override void Dispose()
    {
        if (_computerOpen)
        {
            _computer.Close();
        }
        base.Dispose();
    }

    private void Tick()
    {
        try
        {
            if (!EnsureComputerOpen())
            {
                Volatile.Write(ref _latest, new HardwareMetrics());
                return;
            }

            if (ShouldRescan())
            {
                ScanSensorsIfNeeded(force: true);
            }

            UpdateHardware();

            var cpuTemp = ReadCpuTempValue();
            UpdateCpuTempInvalidTicks(cpuTemp.SensorValid);

            var snapshot = new HardwareMetrics
            {
                CpuTempC = cpuTemp.Value,
                CpuTempSource = cpuTemp.Source,
                GpuUsagePercent = ReadSensorValue(_gpuUsageSensor),
                GpuTempC = ReadSensorValue(_gpuTempSensor)
            };

            Volatile.Write(ref _latest, snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hardware monitor tick failed.");
        }
    }

    private bool EnsureComputerOpen()
    {
        return _computerOpen || TryOpenComputer();
    }

    private bool TryOpenComputer()
    {
        try
        {
            _computer.Open();
            _computerOpen = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LibreHardwareMonitor unavailable.");
            _computerOpen = false;
            return false;
        }
    }

    private bool NeedsRescan()
    {
        return _cpuTempSensor == null || _gpuTempSensor == null || _gpuUsageSensor == null;
    }

    private bool ShouldRescan()
    {
        var now = DateTimeOffset.UtcNow;
        if (_cpuTempInvalidTicks >= CpuTempInvalidThreshold)
        {
            return true;
        }

        if (now - _lastScanAttempt >= RescanInterval)
        {
            return true;
        }

        if (NeedsRescan())
        {
            return now - _lastScanAttempt >= RescanInterval;
        }

        return false;
    }

    private void ScanSensorsIfNeeded(bool force)
    {
        if (!force && !NeedsRescan())
        {
            return;
        }

        if (!EnsureComputerOpen())
        {
            return;
        }

        _lastScanAttempt = DateTimeOffset.UtcNow;
        _cpuHardware.Clear();
        _gpuHardware.Clear();
        _motherboardHardware.Clear();
        _hardwareToUpdate.Clear();
        _cpuTempSensor = null;
        _cpuTempSource = null;
        _gpuTempSensor = null;
        _gpuUsageSensor = null;
        _cpuTempInvalidTicks = 0;

        foreach (var hardware in _computer.Hardware)
        {
            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu:
                    _cpuHardware.Add(hardware);
                    AddHardwareToUpdate(_hardwareToUpdate, hardware);
                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    _gpuHardware.Add(hardware);
                    AddHardwareToUpdate(_hardwareToUpdate, hardware);
                    break;
                case HardwareType.Motherboard:
                    _motherboardHardware.Add(hardware);
                    AddHardwareToUpdate(_hardwareToUpdate, hardware);
                    break;
            }
        }

        UpdateHardware();
        var cpuSelection = FindBestCpuTempSensor(_cpuHardware.Concat(_motherboardHardware));
        if (cpuSelection != null)
        {
            _cpuTempSensor = cpuSelection.Sensor;
            _cpuTempSource = cpuSelection.SourceLabel;
        }

        foreach (var hardware in _gpuHardware)
        {
            var tempSensor = PickGpuTempSensor(hardware);
            var usageSensor = PickGpuUsageSensor(hardware);
            if (tempSensor != null || usageSensor != null)
            {
                _gpuTempSensor = tempSensor;
                _gpuUsageSensor = usageSensor;
                break;
            }
        }
    }

    private void UpdateHardware()
    {
        foreach (var hardware in _hardwareToUpdate)
        {
            UpdateHardwareTree(hardware);
        }
    }

    private static void UpdateHardwareTree(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware)
        {
            UpdateHardwareTree(sub);
        }
    }

    private static IEnumerable<ISensor> EnumerateSensors(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
        {
            yield return sensor;
        }

        foreach (var sub in hardware.SubHardware)
        {
            foreach (var sensor in EnumerateSensors(sub))
            {
                yield return sensor;
            }
        }
    }

    public IReadOnlyList<SensorSnapshot> GetSensorSnapshots()
    {
        if (!EnsureComputerOpen())
        {
            return Array.Empty<SensorSnapshot>();
        }

        var sensors = new List<SensorSnapshot>();
        foreach (var hardware in _computer.Hardware)
        {
            if (!IsDebugHardware(hardware))
            {
                continue;
            }

            UpdateHardwareTree(hardware);
            foreach (var device in EnumerateHardware(hardware))
            {
                foreach (var sensor in device.Sensors)
                {
                    sensors.Add(new SensorSnapshot
                    {
                        HardwareName = device.Name,
                        HardwareType = device.HardwareType.ToString(),
                        SensorName = sensor.Name,
                        SensorType = sensor.SensorType.ToString(),
                        Value = ReadSensorValue(sensor),
                        Identifier = sensor.Identifier?.ToString()
                    });
                }
            }
        }

        AppendWmiSensorSnapshots(sensors);
        return sensors;
    }

    private static IEnumerable<IHardware> EnumerateHardware(IHardware hardware)
    {
        yield return hardware;
        foreach (var sub in hardware.SubHardware)
        {
            foreach (var nested in EnumerateHardware(sub))
            {
                yield return nested;
            }
        }
    }

    private static bool IsDebugHardware(IHardware hardware)
    {
        return hardware.HardwareType == HardwareType.Cpu ||
               hardware.HardwareType == HardwareType.Motherboard ||
               hardware.HardwareType == HardwareType.GpuNvidia ||
               hardware.HardwareType == HardwareType.GpuAmd ||
               hardware.HardwareType == HardwareType.GpuIntel;
    }

    private static void AddHardwareToUpdate(ICollection<IHardware> targets, IHardware hardware)
    {
        if (!targets.Any(existing => ReferenceEquals(existing, hardware)))
        {
            targets.Add(hardware);
        }
    }

    private CpuTempSelection? FindBestCpuTempSensor(IEnumerable<IHardware> hardwares)
    {
        var sensors = new List<ISensor>();
        var candidates = new List<TempSensorCandidate>();
        foreach (var hardware in hardwares)
        {
            foreach (var sensor in EnumerateSensors(hardware))
            {
                if (sensor.SensorType != SensorType.Temperature)
                {
                    continue;
                }

                sensors.Add(sensor);
                if (TryGetValidTemperatureValue(sensor, out var value))
                {
                    candidates.Add(new TempSensorCandidate(sensor, value));
                }
            }
        }

        if (sensors.Count == 0)
        {
            return null;
        }

        var package = sensors.FirstOrDefault(sensor => NameContains(sensor, "Package"));
        if (package != null)
        {
            return new CpuTempSelection(package, package.Name);
        }

        var tctl = sensors.FirstOrDefault(sensor =>
            NameContains(sensor, "Tctl") || NameContains(sensor, "Tdie"));
        if (tctl != null)
        {
            return new CpuTempSelection(tctl, tctl.Name);
        }

        var coreMax = sensors.FirstOrDefault(sensor =>
            NameContains(sensor, "Core Max") || NameContains(sensor, "CCD"));
        if (coreMax != null)
        {
            return new CpuTempSelection(coreMax, coreMax.Name);
        }

        var coreSensors = sensors.Where(sensor => NameContains(sensor, "Core")).ToList();
        if (coreSensors.Count > 0)
        {
            var hottestCore = PickHottestValid(coreSensors) ?? coreSensors[0];
            return new CpuTempSelection(hottestCore, hottestCore.Name);
        }

        var hottest = PickHottestValid(sensors) ?? sensors[0];
        return new CpuTempSelection(hottest, hottest.Name);
    }

    private static ISensor? PickGpuTempSensor(IHardware hardware)
    {
        var sensors = EnumerateSensors(hardware)
            .Where(sensor => sensor.SensorType == SensorType.Temperature)
            .ToList();
        if (sensors.Count == 0)
        {
            return null;
        }

        var preferred = sensors.FirstOrDefault(sensor =>
            sensor.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase));
        return preferred ?? sensors[0];
    }

    private static ISensor? PickGpuUsageSensor(IHardware hardware)
    {
        var sensors = EnumerateSensors(hardware)
            .Where(sensor => sensor.SensorType == SensorType.Load)
            .ToList();
        if (sensors.Count == 0)
        {
            return null;
        }

        var preferred = sensors.FirstOrDefault(sensor =>
            sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase));
        if (preferred != null)
        {
            return preferred;
        }

        preferred = sensors.FirstOrDefault(sensor =>
            sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase));
        if (preferred != null)
        {
            return preferred;
        }

        preferred = sensors.FirstOrDefault(sensor =>
            sensor.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase));
        return preferred ?? sensors[0];
    }

    private static float? ReadSensorValue(ISensor? sensor)
    {
        if (sensor == null)
        {
            return null;
        }

        var value = sensor.Value;
        if (!value.HasValue)
        {
            return null;
        }

        var raw = value.Value;
        if (float.IsNaN(raw) || float.IsInfinity(raw))
        {
            return null;
        }

        return raw;
    }

    private CpuTempReading ReadCpuTempValue()
    {
        if (_cpuTempSensor == null)
        {
            var fallback = ReadCpuTempFallback();
            return new CpuTempReading(fallback.Value, fallback.Source, SensorValid: false);
        }

        if (!TryGetValidTemperatureValue(_cpuTempSensor, out var value))
        {
            var fallback = ReadCpuTempFallback();
            var source = fallback.Source ?? _cpuTempSource;
            return new CpuTempReading(fallback.Value, source, SensorValid: false);
        }

        return new CpuTempReading(MathF.Round(value, 1), _cpuTempSource, SensorValid: true);
    }

    private void UpdateCpuTempInvalidTicks(bool sensorValid)
    {
        if (_cpuTempSensor == null)
        {
            _cpuTempInvalidTicks = 0;
            return;
        }

        if (sensorValid)
        {
            _cpuTempInvalidTicks = 0;
        }
        else
        {
            _cpuTempInvalidTicks++;
        }
    }

    private static bool TryGetValidTemperatureValue(ISensor sensor, out float value)
    {
        value = 0f;
        if (!sensor.Value.HasValue)
        {
            return false;
        }

        var raw = sensor.Value.Value;
        return TryGetValidTemperatureValue(raw, out value);
    }

    private static bool NameContains(ISensor sensor, string token)
    {
        return sensor.Name.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static ISensor? PickHottestValid(IEnumerable<ISensor> sensors)
    {
        ISensor? best = null;
        var bestValue = float.MinValue;
        foreach (var sensor in sensors)
        {
            if (!TryGetValidTemperatureValue(sensor, out var value))
            {
                continue;
            }

            if (value > bestValue)
            {
                bestValue = value;
                best = sensor;
            }
        }

        return best;
    }

    private CpuTempFallback ReadCpuTempFallback()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastCpuFallbackRead < CpuFallbackInterval)
        {
            return new CpuTempFallback(_cachedCpuFallbackTemp, _cachedCpuFallbackSource);
        }

        _lastCpuFallbackRead = now;
        try
        {
            var fallback = TryReadWmiThermalZoneTemperature()
                ?? TryReadWmiPerfThermalZoneTemperature()
                ?? TryReadWmiTemperatureProbe()
                ?? new CpuTempFallback(null, null);
            _cachedCpuFallbackTemp = fallback.Value;
            _cachedCpuFallbackSource = fallback.Source;
            return fallback;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WMI CPU temperature fallback failed.");
            _cachedCpuFallbackTemp = null;
            _cachedCpuFallbackSource = null;
            return new CpuTempFallback(null, null);
        }
    }

    private static CpuTempFallback? TryReadWmiThermalZoneTemperature()
    {
        float? bestTemp = null;
        string? bestSource = null;

        using var searcher = new ManagementObjectSearcher(
            "root\\WMI",
            "SELECT CurrentTemperature, InstanceName FROM MSAcpi_ThermalZoneTemperature");
        using var results = searcher.Get();
        foreach (var entry in results)
        {
            if (entry is not ManagementObject obj)
            {
                continue;
            }

            var raw = obj["CurrentTemperature"];
            if (!TryConvertRawTemperature(raw, out var tempC))
            {
                continue;
            }

            if (bestTemp == null || tempC > bestTemp)
            {
                bestTemp = tempC;
                var name = obj["InstanceName"] as string;
                bestSource = string.IsNullOrWhiteSpace(name) ? "WMI Thermal Zone" : $"WMI {name}";
            }
        }

        if (bestTemp.HasValue)
        {
            return new CpuTempFallback(MathF.Round(bestTemp.Value, 1), bestSource);
        }

        return null;
    }

    private static CpuTempFallback? TryReadWmiPerfThermalZoneTemperature()
    {
        float? bestTemp = null;
        string? bestSource = null;

        using var searcher = new ManagementObjectSearcher(
            "root\\CIMV2",
            "SELECT Temperature, Name, InstanceName FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
        using var results = searcher.Get();
        foreach (var entry in results)
        {
            if (entry is not ManagementObject obj)
            {
                continue;
            }

            var raw = obj["Temperature"];
            if (!TryConvertRawTemperature(raw, out var tempC))
            {
                continue;
            }

            if (bestTemp == null || tempC > bestTemp)
            {
                bestTemp = tempC;
                var name = obj["InstanceName"] as string ?? obj["Name"] as string;
                bestSource = string.IsNullOrWhiteSpace(name) ? "WMI Thermal Zone Info" : $"WMI {name}";
            }
        }

        if (bestTemp.HasValue)
        {
            return new CpuTempFallback(MathF.Round(bestTemp.Value, 1), bestSource);
        }

        return null;
    }

    private static CpuTempFallback? TryReadWmiTemperatureProbe()
    {
        float? bestTemp = null;
        string? bestSource = null;

        using var searcher = new ManagementObjectSearcher(
            "root\\CIMV2",
            "SELECT CurrentReading, Name, Description FROM Win32_TemperatureProbe");
        using var results = searcher.Get();
        foreach (var entry in results)
        {
            if (entry is not ManagementObject obj)
            {
                continue;
            }

            var raw = obj["CurrentReading"];
            if (!TryConvertRawTemperature(raw, out var tempC))
            {
                continue;
            }

            if (bestTemp == null || tempC > bestTemp)
            {
                bestTemp = tempC;
                var name = obj["Name"] as string ?? obj["Description"] as string;
                bestSource = string.IsNullOrWhiteSpace(name) ? "WMI Temperature Probe" : $"WMI {name}";
            }
        }

        if (bestTemp.HasValue)
        {
            return new CpuTempFallback(MathF.Round(bestTemp.Value, 1), bestSource);
        }

        return null;
    }

    private static bool TryConvertRawTemperature(object? raw, out float tempC)
    {
        tempC = 0f;
        if (raw == null)
        {
            return false;
        }

        if (!TryGetNumericValue(raw, out var numeric))
        {
            return false;
        }

        double celsius;
        if (numeric > 1000d)
        {
            celsius = (numeric / 10d) - 273.15d;
        }
        else if (numeric > 170d)
        {
            celsius = numeric - 273.15d;
        }
        else
        {
            celsius = numeric;
        }

        if (double.IsNaN(celsius) || double.IsInfinity(celsius))
        {
            return false;
        }

        return TryGetValidTemperatureValue((float)celsius, out tempC);
    }

    private static bool TryGetNumericValue(object raw, out double value)
    {
        value = 0d;
        switch (raw)
        {
            case ushort rawValue:
                value = rawValue;
                return true;
            case uint rawValue:
                value = rawValue;
                return true;
            case int rawValue:
                value = rawValue;
                return true;
            case long rawValue:
                value = rawValue;
                return true;
            case string text when double.TryParse(text, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static void AppendWmiSensorSnapshots(ICollection<SensorSnapshot> sensors)
    {
        AppendWmiThermalZoneSnapshots(sensors);
        AppendWmiPerfThermalZoneSnapshots(sensors);
        AppendWmiTemperatureProbeSnapshots(sensors);
    }

    private static void AppendWmiThermalZoneSnapshots(ICollection<SensorSnapshot> sensors)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\WMI",
                "SELECT CurrentTemperature, InstanceName FROM MSAcpi_ThermalZoneTemperature");
            using var results = searcher.Get();
            foreach (var entry in results)
            {
                if (entry is not ManagementObject obj)
                {
                    continue;
                }

                var raw = obj["CurrentTemperature"];
                if (!TryConvertRawTemperature(raw, out var tempC))
                {
                    continue;
                }

                var name = obj["InstanceName"] as string ?? "Thermal Zone";
                sensors.Add(new SensorSnapshot
                {
                    HardwareName = "WMI Thermal Zone",
                    HardwareType = "Wmi",
                    SensorName = name,
                    SensorType = "Temperature",
                    Value = MathF.Round(tempC, 1),
                    Identifier = obj.Path?.Path
                });
            }
        }
        catch
        {
            // Ignore WMI errors in debug listing.
        }
    }

    private static void AppendWmiPerfThermalZoneSnapshots(ICollection<SensorSnapshot> sensors)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT Temperature, Name, InstanceName FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            using var results = searcher.Get();
            foreach (var entry in results)
            {
                if (entry is not ManagementObject obj)
                {
                    continue;
                }

                var raw = obj["Temperature"];
                if (!TryConvertRawTemperature(raw, out var tempC))
                {
                    continue;
                }

                var name = obj["InstanceName"] as string ?? obj["Name"] as string ?? "Thermal Zone Info";
                sensors.Add(new SensorSnapshot
                {
                    HardwareName = "WMI Thermal Zone Info",
                    HardwareType = "Wmi",
                    SensorName = name,
                    SensorType = "Temperature",
                    Value = MathF.Round(tempC, 1),
                    Identifier = obj.Path?.Path
                });
            }
        }
        catch
        {
            // Ignore WMI errors in debug listing.
        }
    }

    private static void AppendWmiTemperatureProbeSnapshots(ICollection<SensorSnapshot> sensors)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT CurrentReading, Name, Description FROM Win32_TemperatureProbe");
            using var results = searcher.Get();
            foreach (var entry in results)
            {
                if (entry is not ManagementObject obj)
                {
                    continue;
                }

                var raw = obj["CurrentReading"];
                if (!TryConvertRawTemperature(raw, out var tempC))
                {
                    continue;
                }

                var name = obj["Name"] as string ?? obj["Description"] as string ?? "Temperature Probe";
                sensors.Add(new SensorSnapshot
                {
                    HardwareName = "WMI Temperature Probe",
                    HardwareType = "Wmi",
                    SensorName = name,
                    SensorType = "Temperature",
                    Value = MathF.Round(tempC, 1),
                    Identifier = obj.Path?.Path
                });
            }
        }
        catch
        {
            // Ignore WMI errors in debug listing.
        }
    }

    private static bool TryGetValidTemperatureValue(float raw, out float value)
    {
        value = 0f;
        if (float.IsNaN(raw) || float.IsInfinity(raw))
        {
            return false;
        }

        if (raw < 0f || raw > 120f)
        {
            return false;
        }

        value = raw;
        return true;
    }

    private sealed record CpuTempSelection(ISensor Sensor, string SourceLabel);
    private readonly record struct TempSensorCandidate(ISensor Sensor, float Value);
    private readonly record struct CpuTempFallback(float? Value, string? Source);
    private readonly record struct CpuTempReading(float? Value, string? Source, bool SensorValid);
}

public sealed record HardwareMetrics
{
    public float? CpuTempC { get; init; }
    public string? CpuTempSource { get; init; }
    public float? GpuUsagePercent { get; init; }
    public float? GpuTempC { get; init; }
}

public sealed record SensorSnapshot
{
    public string HardwareName { get; init; } = string.Empty;
    public string HardwareType { get; init; } = string.Empty;
    public string SensorName { get; init; } = string.Empty;
    public string SensorType { get; init; } = string.Empty;
    public float? Value { get; init; }
    public string? Identifier { get; init; }
}
