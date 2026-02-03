using LibreHardwareMonitor.Hardware;
using System.Globalization;
using System.Management;
using System.Security.Principal;
using System.Threading;
using Agent.Services.CpuTempProviders;

namespace Agent.Services;

public sealed class HardwareMonitorService : BackgroundService
{
    private const int CpuTempInvalidThreshold = 5;
    private const int CpuTempWarmupTicks = 5;
    private const int CpuTempNoValueThreshold = 10;
    private const string CpuTempStatusOk = "ok";
    private const string CpuTempStatusNoSensors = "no_sensors";
    private const string CpuTempStatusNoValues = "no_values";
    private const string CpuTempStatusWarmingUp = "warming_up";
    private const string CpuTempProviderName = "lhm";
    private const string CpuTempWarmupHint = "Sensor value not available yet.";
    private const string CpuTempNoValueHint = "Sensor value not available yet. Try running as administrator.";
    private readonly TimeSpan _pollInterval;
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<HardwareMonitorService> _logger;
    private readonly Computer _computer;
    private readonly UpdateVisitor _updateVisitor = new();
    private readonly bool _isAdmin;
    private DateTimeOffset _lastScanAttempt = DateTimeOffset.MinValue;
    private bool _computerOpen;

    private readonly List<IHardware> _cpuHardware = new();
    private readonly List<IHardware> _gpuHardware = new();
    private readonly List<IHardware> _motherboardHardware = new();
    private ISensor? _cpuTempSensor;
    private string? _cpuTempSource;
    private ISensor? _cpuTempDistanceSensor;
    private string? _cpuTempDistanceSource;
    private float? _cpuTempDistanceTjMax;
    private float? _lastCpuTempSensorValue;
    private float? _lastCpuTempDistanceValue;
    private bool _cpuTempSensorsPresent;
    private ISensor? _gpuTempSensor;
    private ISensor? _gpuUsageSensor;
    private int _cpuTempInvalidTicks;
    private int _cpuTempWarmupTicksRemaining;
    private int _ticksSinceValidCpuTemp;
    private float? _lastValidCpuTempC;
    private int _cpuTempSensorsFound;
    private int _cpuTempSensorsWithValue;

    private volatile HardwareMetrics _latest = new()
    {
        CpuTempC = null,
        CpuTempSource = null,
        CpuTempStatus = CpuTempStatusNoSensors,
        CpuTempDetails = null,
        GpuUsagePercent = null,
        GpuTempC = null
    };
    private volatile CpuTempResult _latestCpuTemp = new()
    {
        TempC = null,
        Status = CpuTempStatusNoSensors,
        Hint = null,
        Provider = CpuTempProviderName
    };

    public HardwareMonitorService(ILogger<HardwareMonitorService> logger, MonitorSettings settings)
    {
        _logger = logger;
        _pollInterval = TimeSpan.FromMilliseconds(settings.HardwareIntervalMs);
        _isAdmin = IsProcessElevated();
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

    public CpuTempResult GetLatestCpuTempResult()
    {
        return Volatile.Read(ref _latestCpuTemp);
    }

    public CpuTempDebugSnapshot GetCpuTempDebugSnapshot()
    {
        var latestMetrics = Volatile.Read(ref _latest);
        var latestCpuTemp = Volatile.Read(ref _latestCpuTemp);
        return new CpuTempDebugSnapshot
        {
            TempC = latestCpuTemp.TempC,
            Source = latestMetrics.CpuTempSource,
            Status = latestCpuTemp.Status,
            Provider = latestCpuTemp.Provider,
            Hint = latestCpuTemp.Hint,
            Details = latestMetrics.CpuTempDetails
        };
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
                var blockedSnapshot = new CpuTempResult
                {
                    TempC = null,
                    Status = CpuTempStatusNoSensors,
                    Hint = null,
                    Provider = CpuTempProviderName
                };
                Volatile.Write(ref _latestCpuTemp, blockedSnapshot);
                Volatile.Write(ref _latest, new HardwareMetrics
                {
                    CpuTempStatus = CpuTempStatusNoSensors,
                    CpuTempDetails = BuildCpuTempDiagnostics(hint: null)
                });
                return;
            }

            if (ShouldRescan())
            {
                ScanSensorsIfNeeded(force: true);
            }

            UpdateHardware();

            var cpuTemp = ReadCpuTempValue();
            UpdateCpuTempInvalidTicks(cpuTemp.SensorValid);
            UpdateCpuTempHistory(cpuTemp);
            var cpuTempStatus = GetCpuTempStatus(cpuTemp, out var cpuTempHint);
            var cpuTempResult = new CpuTempResult
            {
                TempC = cpuTemp.Value,
                Status = cpuTempStatus,
                Hint = cpuTempHint,
                Provider = CpuTempProviderName
            };

            var snapshot = new HardwareMetrics
            {
                CpuTempC = cpuTemp.Value,
                CpuTempSource = cpuTemp.Source,
                CpuTempStatus = cpuTempStatus,
                CpuTempDetails = BuildCpuTempDiagnostics(cpuTempHint),
                GpuUsagePercent = ReadSensorValue(_gpuUsageSensor),
                GpuTempC = ReadSensorValue(_gpuTempSensor)
            };

            Volatile.Write(ref _latestCpuTemp, cpuTempResult);
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
        _cpuTempSensor = null;
        _cpuTempSource = null;
        _cpuTempDistanceSensor = null;
        _cpuTempDistanceSource = null;
        _cpuTempDistanceTjMax = null;
        _lastCpuTempSensorValue = null;
        _lastCpuTempDistanceValue = null;
        _cpuTempSensorsPresent = false;
        _cpuTempSensorsFound = 0;
        _cpuTempSensorsWithValue = 0;
        _gpuTempSensor = null;
        _gpuUsageSensor = null;
        _cpuTempInvalidTicks = 0;
        _cpuTempWarmupTicksRemaining = CpuTempWarmupTicks;
        _ticksSinceValidCpuTemp = 0;

        foreach (var hardware in _computer.Hardware)
        {
            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu:
                    _cpuHardware.Add(hardware);
                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    _gpuHardware.Add(hardware);
                    break;
                case HardwareType.Motherboard:
                    _motherboardHardware.Add(hardware);
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
        _computer.Accept(_updateVisitor);
    }

    private void UpdateHardwareNode(IHardware hardware)
    {
        hardware.Accept(_updateVisitor);
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

        _computer.Accept(_updateVisitor);
        var sensors = new List<SensorSnapshot>();
        foreach (var hardware in _computer.Hardware)
        {
            if (!IsDebugHardware(hardware))
            {
                continue;
            }

            ActivateTemperatureSensors(hardware);
            UpdateHardwareNode(hardware);
            if (hardware.HardwareType == HardwareType.Cpu || hardware.HardwareType == HardwareType.Motherboard)
            {
                UpdateHardwareNode(hardware);
            }
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
                        HasValue = sensor.Value.HasValue,
                        RawValue = FormatRawSensorValue(sensor),
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

    private CpuTempSelection? FindBestCpuTempSensor(IEnumerable<IHardware> hardwares)
    {
        var sensors = new List<ISensor>();
        var sensorsWithValue = new List<ISensor>();
        var sensorsFound = 0;
        var sensorsWithValueCount = 0;
        foreach (var hardware in hardwares)
        {
            ActivateTemperatureSensors(hardware);
            UpdateHardwareNode(hardware);
            foreach (var sensor in EnumerateSensors(hardware))
            {
                if (sensor.SensorType != SensorType.Temperature)
                {
                    continue;
                }

                sensorsFound++;
                if (TryGetValidTemperatureValue(sensor, out _))
                {
                    sensorsWithValueCount++;
                    sensorsWithValue.Add(sensor);
                }
                sensors.Add(sensor);
            }
        }

        _cpuTempSensorsFound = sensorsFound;
        _cpuTempSensorsWithValue = sensorsWithValueCount;
        _cpuTempSensorsPresent = sensorsFound > 0;
        SelectDistanceToTjMaxSensor(sensors);
        if (!_cpuTempSensorsPresent)
        {
            return null;
        }

        var preferred = PickPreferredCpuSensor(sensorsWithValue);
        if (preferred == null)
        {
            preferred = PickPreferredCpuSensor(sensors);
        }

        return preferred == null ? null : new CpuTempSelection(preferred, preferred.Name);
    }

    private void SelectDistanceToTjMaxSensor(IReadOnlyList<ISensor> sensors)
    {
        _cpuTempDistanceSensor = null;
        _cpuTempDistanceSource = null;
        _cpuTempDistanceTjMax = null;

        if (sensors.Count == 0)
        {
            return;
        }

        ISensor? preferred = null;
        for (var i = 0; i < sensors.Count; i++)
        {
            var sensor = sensors[i];
            if (!IsDistanceToTjMaxSensor(sensor))
            {
                continue;
            }

            preferred ??= sensor;
            if (NameContains(sensor, "Core Max"))
            {
                preferred = sensor;
                break;
            }
        }

        if (preferred != null && TryGetTjMax(preferred, out var preferredTjMax))
        {
            _cpuTempDistanceSensor = preferred;
            _cpuTempDistanceTjMax = preferredTjMax;
            _cpuTempDistanceSource = BuildDistanceSourceLabel(preferred, preferredTjMax);
            return;
        }

        for (var i = 0; i < sensors.Count; i++)
        {
            var sensor = sensors[i];
            if (!IsDistanceToTjMaxSensor(sensor))
            {
                continue;
            }

            if (TryGetTjMax(sensor, out var tjMax))
            {
                _cpuTempDistanceSensor = sensor;
                _cpuTempDistanceTjMax = tjMax;
                _cpuTempDistanceSource = BuildDistanceSourceLabel(sensor, tjMax);
                return;
            }
        }
    }

    private static ISensor? PickPreferredCpuSensor(IReadOnlyList<ISensor> sensors)
    {
        if (sensors.Count == 0)
        {
            return null;
        }

        for (var i = 0; i < sensors.Count; i++)
        {
            if (NameEquals(sensors[i], "CPU Package"))
            {
                return sensors[i];
            }
        }

        for (var i = 0; i < sensors.Count; i++)
        {
            if (NameEquals(sensors[i], "Core Max"))
            {
                return sensors[i];
            }
        }

        for (var i = 0; i < sensors.Count; i++)
        {
            if (NameEquals(sensors[i], "Tctl") || NameEquals(sensors[i], "Tdie") ||
                NameContains(sensors[i], "Tctl") || NameContains(sensors[i], "Tdie"))
            {
                return sensors[i];
            }
        }

        var coreIndex = -1;
        for (var i = 0; i < sensors.Count; i++)
        {
            if (NameContains(sensors[i], "Core"))
            {
                coreIndex = i;
                break;
            }
        }

        if (coreIndex >= 0)
        {
            var coreSensors = new List<ISensor>();
            for (var i = coreIndex; i < sensors.Count; i++)
            {
                if (NameContains(sensors[i], "Core"))
                {
                    coreSensors.Add(sensors[i]);
                }
            }

            var hottestCore = PickHottestValid(coreSensors) ?? coreSensors[0];
            return hottestCore;
        }

        return PickHottestValid(sensors) ?? sensors[0];
    }

    private static bool IsDistanceToTjMaxSensor(ISensor sensor)
    {
        return sensor.SensorType == SensorType.Temperature &&
               NameContains(sensor, "Distance") &&
               NameContains(sensor, "TJMax");
    }

    private static string BuildDistanceSourceLabel(ISensor sensor, float tjMax)
    {
        return $"{sensor.Name} (TJMax {tjMax:0.#}C)";
    }

    private static bool TryGetTjMax(ISensor sensor, out float tjMax)
    {
        tjMax = 0f;
        try
        {
            foreach (var parameter in sensor.Parameters)
            {
                if (parameter == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(parameter.Name) &&
                    parameter.Name.Contains("TJMax", StringComparison.OrdinalIgnoreCase))
                {
                    return TryGetValidTjMax(parameter.Value, out tjMax);
                }
            }
        }
        catch
        {
            // Ignore sensor parameter failures.
        }

        return false;
    }

    private static bool TryGetValidTjMax(float raw, out float value)
    {
        value = 0f;
        if (float.IsNaN(raw) || float.IsInfinity(raw))
        {
            return false;
        }

        if (raw < 60f || raw > 130f)
        {
            return false;
        }

        value = raw;
        return true;
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
        _lastCpuTempSensorValue = ReadSensorValue(_cpuTempSensor);
        _lastCpuTempDistanceValue = ReadSensorValue(_cpuTempDistanceSensor);

        if (_cpuTempSensor != null && TryGetValidTemperatureValue(_cpuTempSensor, out var value))
        {
            if (_cpuTempSensorsFound == 0)
            {
                _cpuTempSensorsFound = 1;
                _cpuTempSensorsPresent = true;
            }

            if (_cpuTempSensorsWithValue == 0)
            {
                _cpuTempSensorsWithValue = 1;
            }

            return new CpuTempReading(MathF.Round(value, 1), _cpuTempSource, SensorValid: true);
        }

        if (_cpuTempDistanceSensor != null &&
            _cpuTempDistanceTjMax.HasValue &&
            _lastCpuTempDistanceValue.HasValue)
        {
            var derivedRaw = _cpuTempDistanceTjMax.Value - _lastCpuTempDistanceValue.Value;
            if (TryGetValidTemperatureValue(derivedRaw, out var derivedValue))
            {
                if (_cpuTempSensorsFound == 0)
                {
                    _cpuTempSensorsFound = 1;
                    _cpuTempSensorsPresent = true;
                }

                if (_cpuTempSensorsWithValue == 0)
                {
                    _cpuTempSensorsWithValue = 1;
                }

                var sourceLabel = _cpuTempDistanceSource ?? _cpuTempSource;
                return new CpuTempReading(MathF.Round(derivedValue, 1), sourceLabel, SensorValid: true);
            }
        }

        if (_cpuTempSensorsPresent)
        {
            var source = _cpuTempSource ?? _cpuTempDistanceSource ?? "CPU sensors present but no values";
            return new CpuTempReading(null, source, SensorValid: false);
        }

        return new CpuTempReading(null, null, SensorValid: false);
    }

    private void UpdateCpuTempInvalidTicks(bool sensorValid)
    {
        if (_cpuTempSensor == null && _cpuTempDistanceSensor == null)
        {
            _cpuTempInvalidTicks = 0;
            return;
        }

        if (sensorValid)
        {
            _cpuTempInvalidTicks = 0;
            return;
        }

        if (_cpuTempWarmupTicksRemaining > 0)
        {
            return;
        }

        if (!_lastValidCpuTempC.HasValue)
        {
            return;
        }

        _cpuTempInvalidTicks++;
    }

    private void UpdateCpuTempHistory(CpuTempReading reading)
    {
        if (reading.SensorValid && reading.Value.HasValue)
        {
            _ticksSinceValidCpuTemp = 0;
            _lastValidCpuTempC = reading.Value;
            _cpuTempWarmupTicksRemaining = 0;
            return;
        }

        if (_cpuTempWarmupTicksRemaining > 0)
        {
            _cpuTempWarmupTicksRemaining--;
            return;
        }

        if (_ticksSinceValidCpuTemp < int.MaxValue)
        {
            _ticksSinceValidCpuTemp++;
        }
    }

    private string GetCpuTempStatus(CpuTempReading reading, out string? hint)
    {
        hint = null;

        if (reading.SensorValid && reading.Value.HasValue)
        {
            return CpuTempStatusOk;
        }

        if (_cpuTempSensorsFound <= 0)
        {
            return CpuTempStatusNoSensors;
        }

        if (_cpuTempWarmupTicksRemaining > 0)
        {
            hint = CpuTempWarmupHint;
            return CpuTempStatusWarmingUp;
        }

        if (_ticksSinceValidCpuTemp >= CpuTempNoValueThreshold)
        {
            hint = CpuTempNoValueHint;
            return CpuTempStatusNoValues;
        }

        hint = CpuTempWarmupHint;
        return CpuTempStatusWarmingUp;
    }

    private CpuTempDiagnostics BuildCpuTempDiagnostics(string? hint)
    {
        return new CpuTempDiagnostics
        {
            IsAdmin = _isAdmin,
            CpuTempSensorsFound = _cpuTempSensorsFound,
            CpuTempSensorsWithValue = _cpuTempSensorsWithValue,
            LastValidCpuTempC = _lastValidCpuTempC,
            TicksSinceValid = _ticksSinceValidCpuTemp,
            WarmupTicksRemaining = _cpuTempWarmupTicksRemaining,
            SelectedSensorName = _cpuTempSensor?.Name,
            SelectedSensorIdentifier = _cpuTempSensor?.Identifier?.ToString(),
            SelectedSensorValue = _lastCpuTempSensorValue,
            DerivedSensorName = _cpuTempDistanceSensor?.Name,
            DerivedSensorValue = _lastCpuTempDistanceValue,
            DerivedSensorTjMax = _cpuTempDistanceTjMax,
            Hint = hint
        };
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

    private static bool NameEquals(ISensor sensor, string token)
    {
        return string.Equals(sensor.Name, token, StringComparison.OrdinalIgnoreCase);
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

    private static void ActivateTemperatureSensors(IHardware hardware)
    {
        foreach (var sensor in EnumerateSensors(hardware))
        {
            if (sensor.SensorType == SensorType.Temperature)
            {
                TryActivateSensor(sensor);
            }
        }
    }

    private static void TryActivateSensor(ISensor sensor)
    {
        if (TrySetSensorBoolProperty(sensor, "IsActive"))
        {
            return;
        }

        if (TrySetSensorBoolProperty(sensor, "Active"))
        {
            return;
        }

        if (TryInvokeSensorMethod(sensor, "Activate"))
        {
            return;
        }

        TryInvokeSensorMethod(sensor, "SetActive", true);
        TrySetSensorBoolField(sensor, "active");
        TrySetSensorBoolField(sensor, "_active");
        TrySetSensorBoolField(sensor, "isActive");
        TrySetSensorBoolField(sensor, "_isActive");
    }

    private static bool TrySetSensorBoolProperty(ISensor sensor, string propertyName)
    {
        var property = sensor.GetType().GetProperty(
            propertyName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);

        if (property is not { CanWrite: true } || property.PropertyType != typeof(bool))
        {
            return false;
        }

        property.SetValue(sensor, true);
        return true;
    }

    private static bool TryInvokeSensorMethod(ISensor sensor, string methodName, params object[]? args)
    {
        var method = sensor.GetType().GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        if (method == null)
        {
            return false;
        }

        var parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            method.Invoke(sensor, null);
            return true;
        }

        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool))
        {
            method.Invoke(sensor, new object[] { true });
            return true;
        }

        if (args != null)
        {
            method.Invoke(sensor, args);
            return true;
        }

        return false;
    }

    private static bool TrySetSensorBoolField(ISensor sensor, string fieldName)
    {
        var field = sensor.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.IgnoreCase);
        if (field == null || field.FieldType != typeof(bool))
        {
            return false;
        }

        field.SetValue(sensor, true);
        return true;
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
                    HasValue = true,
                    RawValue = tempC.ToString("0.###", CultureInfo.InvariantCulture),
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
                    HasValue = true,
                    RawValue = tempC.ToString("0.###", CultureInfo.InvariantCulture),
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
                    HasValue = true,
                    RawValue = tempC.ToString("0.###", CultureInfo.InvariantCulture),
                    Identifier = obj.Path?.Path
                });
            }
        }
        catch
        {
            // Ignore WMI errors in debug listing.
        }
    }

    private static string? FormatRawSensorValue(ISensor sensor)
    {
        if (!sensor.Value.HasValue)
        {
            return null;
        }

        var raw = sensor.Value.Value;
        if (float.IsNaN(raw))
        {
            return "NaN";
        }

        if (float.IsPositiveInfinity(raw))
        {
            return "+Infinity";
        }

        if (float.IsNegativeInfinity(raw))
        {
            return "-Infinity";
        }

        return raw.ToString("0.###", CultureInfo.InvariantCulture);
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

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer)
        {
            computer.Traverse(this);
        }

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }

    private sealed record CpuTempSelection(ISensor Sensor, string SourceLabel);
    private readonly record struct CpuTempReading(float? Value, string? Source, bool SensorValid);
}

public sealed record HardwareMetrics
{
    public float? CpuTempC { get; init; }
    public string? CpuTempSource { get; init; }
    public string? CpuTempStatus { get; init; }
    public CpuTempDiagnostics? CpuTempDetails { get; init; }
    public float? GpuUsagePercent { get; init; }
    public float? GpuTempC { get; init; }
}

public sealed record CpuTempDebugSnapshot
{
    public float? TempC { get; init; }
    public string? Source { get; init; }
    public string? Status { get; init; }
    public string? Provider { get; init; }
    public string? Hint { get; init; }
    public CpuTempDiagnostics? Details { get; init; }
}

public sealed record SensorSnapshot
{
    public string HardwareName { get; init; } = string.Empty;
    public string HardwareType { get; init; } = string.Empty;
    public string SensorName { get; init; } = string.Empty;
    public string SensorType { get; init; } = string.Empty;
    public float? Value { get; init; }
    public bool HasValue { get; init; }
    public string? RawValue { get; init; }
    public string? Identifier { get; init; }
}
