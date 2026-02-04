using System.Management;

namespace Agent.Services.CpuTempProviders;

public sealed class WmiThermalZoneCpuTempProvider : ICpuTempProvider
{
    private const string StatusValue = "wmi_approx";
    private const string ProviderValue = "wmi";
    private const string HintValue = "WMI ThermalZone is often inaccurate; use only as last resort.";
    private readonly TimeSpan _pollInterval;
    private DateTimeOffset _lastRead = DateTimeOffset.MinValue;
    private CpuTempResult _cached = new()
    {
        TempC = null,
        Status = StatusValue,
        Hint = HintValue,
        Provider = ProviderValue
    };

    public WmiThermalZoneCpuTempProvider(MonitorSettings settings)
    {
        _pollInterval = TimeSpan.FromMilliseconds(settings.HardwareIntervalMs);
    }

    public Task<CpuTempResult> GetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        if (now - _lastRead < _pollInterval)
        {
            return Task.FromResult(_cached);
        }

        _lastRead = now;
        var result = ReadThermalZone();
        _cached = result;
        return Task.FromResult(result);
    }

    private static CpuTempResult ReadThermalZone()
    {
        float? bestTemp = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            using var results = searcher.Get();
            foreach (var entry in results)
            {
                using var obj = entry as ManagementObject;
                if (obj == null)
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
                }
            }
        }
        catch
        {
            bestTemp = null;
        }

        return new CpuTempResult
        {
            TempC = bestTemp.HasValue ? MathF.Round(bestTemp.Value, 1) : null,
            Status = StatusValue,
            Hint = HintValue,
            Provider = ProviderValue
        };
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
}
