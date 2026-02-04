using System.Management;

namespace Agent.Services;

public static class SystemMemoryInfo
{
    private const double BytesPerMb = 1024d * 1024d;
    private static readonly Lazy<TotalMemorySnapshot> TotalMemory = new(ReadTotalMemory);

    public static TotalMemorySnapshot GetTotalMemorySnapshot() => TotalMemory.Value;

    private static TotalMemorySnapshot ReadTotalMemory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            using var results = searcher.Get();
            foreach (var entry in results)
            {
                using var obj = entry as ManagementObject;
                if (obj == null)
                {
                    continue;
                }

                var raw = obj["TotalPhysicalMemory"];
                if (raw == null)
                {
                    continue;
                }

                if (!TryParseBytes(raw, out var totalBytes) || totalBytes <= 0)
                {
                    continue;
                }

                var totalMb = (int)Math.Round(totalBytes / BytesPerMb);
                if (totalMb <= 0)
                {
                    return new TotalMemorySnapshot(null, "Total physical memory not available.");
                }

                return new TotalMemorySnapshot(totalMb, null);
            }

            return new TotalMemorySnapshot(null, "Total physical memory not available.");
        }
        catch (Exception ex)
        {
            return new TotalMemorySnapshot(null, $"Total physical memory read failed: {ex.Message}");
        }
    }

    private static bool TryParseBytes(object raw, out ulong totalBytes)
    {
        switch (raw)
        {
            case ulong bytes:
                totalBytes = bytes;
                return true;
            case long bytes when bytes > 0:
                totalBytes = (ulong)bytes;
                return true;
            case string text when ulong.TryParse(text, out var parsed):
                totalBytes = parsed;
                return true;
            default:
                totalBytes = 0;
                return false;
        }
    }

    public sealed record TotalMemorySnapshot(int? TotalMb, string? Error);
}
