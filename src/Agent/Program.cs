using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Services;
using Agent.Services.CpuTempProviders;

var builder = WebApplication.CreateBuilder(args);

var monitorSettings = new MonitorSettings();
builder.Configuration.Bind(monitorSettings);
monitorSettings.ApplyEnvironmentOverrides();
monitorSettings.Normalize();

// Bind to all interfaces so tablets can connect via LAN.
builder.WebHost.UseUrls($"http://0.0.0.0:{monitorSettings.Port}");

builder.Services.AddSingleton(monitorSettings);
builder.Services.AddSingleton<HardwareMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HardwareMonitorService>());
builder.Services.AddSingleton<RuntimeStats>();
builder.Services.AddSingleton<LhmCpuTempProvider>();
builder.Services.AddSingleton<WmiThermalZoneCpuTempProvider>();
builder.Services.AddSingleton<ExternalCpuTempProvider>();
builder.Services.AddSingleton<MetricsCollector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsCollector>());
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowLocalNetwork", policy =>
    {
        policy
            .SetIsOriginAllowed(IsLocalNetworkOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

if (monitorSettings.AllowLocalNetworkCors)
{
    app.UseCors("AllowLocalNetwork");
}

app.UseDefaultFiles();
app.UseStaticFiles();

// Enable WebSockets for realtime updates.
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.MapGet("/api/health", () => Results.Json(new
{
    ok = true,
    time = DateTimeOffset.UtcNow.ToString("O")
}, jsonOptions));

app.MapGet("/api/metrics", (MetricsCollector collector) =>
    Results.Json(collector.GetLatestSnapshot(), jsonOptions));

app.MapGet("/api/sensors", (HardwareMonitorService monitor) =>
{
    try
    {
        var sensors = monitor.GetSensorSnapshots();
        return Results.Json(new { ok = true, sensors }, jsonOptions);
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, jsonOptions);
    }
});

app.MapGet("/api/cpu-temp-debug", (HardwareMonitorService monitor) =>
    Results.Json(new { ok = true, cpuTemp = monitor.GetCpuTempDebugSnapshot() }, jsonOptions));

app.MapGet("/api/stats", (RuntimeStats stats) =>
    Results.Json(stats.GetSnapshot(), jsonOptions));

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var metricsCollector = context.RequestServices.GetRequiredService<MetricsCollector>();
    var runtimeStats = context.RequestServices.GetRequiredService<RuntimeStats>();
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    runtimeStats.ClientConnected();
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var sendToken = linkedCts.Token;

    var receiveTask = Task.Run(async () =>
    {
        var buffer = new byte[4 * 1024];
        try
        {
            while (!sendToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, sendToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    linkedCts.Cancel();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown.
        }
        catch (WebSocketException)
        {
            linkedCts.Cancel();
        }
    }, CancellationToken.None);

    try
    {
        var initBytes = JsonSerializer.SerializeToUtf8Bytes(
            new WsEnvelope<MetricsSnapshot>("init", metricsCollector.GetLatestSnapshot(includeSeries: true)),
            jsonOptions);
        await socket.SendAsync(initBytes, WebSocketMessageType.Text, true, sendToken);

        var tick = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(monitorSettings.MetricsIntervalMs));
        while (await timer.WaitForNextTickAsync(sendToken))
        {
            if (socket.State != WebSocketState.Open)
            {
                break;
            }

            var sendSeries = tick > 0 && tick % 5 == 0;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new WsEnvelope<MetricsSnapshot>(sendSeries ? "series" : "metrics",
                    metricsCollector.GetLatestSnapshot(includeSeries: sendSeries)),
                jsonOptions);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, sendToken);
            tick++;
        }
    }
    catch (OperationCanceledException)
    {
        // Normal during shutdown or client close.
    }
    catch (WebSocketException)
    {
        // Client disconnect or transport issue.
    }
    catch (ObjectDisposedException)
    {
        // Socket disposed during shutdown.
    }
    finally
    {
        runtimeStats.ClientDisconnected();
        try
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // Client already disconnected.
        }
        finally
        {
            linkedCts.Cancel();
            try
            {
                await receiveTask;
            }
            catch (Exception)
            {
                // Ignore receive errors on shutdown.
            }
        }
    }
});

app.Run();

static bool IsLocalNetworkOrigin(string origin)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
    {
        return false;
    }

    if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (!IPAddress.TryParse(uri.Host, out var address))
    {
        return false;
    }

    if (IPAddress.IsLoopback(address))
    {
        return true;
    }

    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
    {
        var bytes = address.GetAddressBytes();
        var isUniqueLocal = (bytes[0] & 0xFE) == 0xFC;
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || isUniqueLocal;
    }

    return false;
}

public sealed class MonitorSettings
{
    public int Port { get; set; } = 8787;
    public int MetricsIntervalMs { get; set; } = 1000;
    public int MetricsIntervalNoClientsMs { get; set; } = 2000;
    public int HardwareIntervalMs { get; set; } = 2000;
    public bool AllowLocalNetworkCors { get; set; } = false;
    public bool AdaptiveUpdateNoClients { get; set; } = false;
    public string CpuTempProvider { get; set; } = "lhm";
    public bool CpuTempFallbackToWmiApprox { get; set; } = false;

    public void ApplyEnvironmentOverrides()
    {
        Port = ReadIntEnv("MONITOR_PORT", Port);
        MetricsIntervalMs = ReadIntEnv("MONITOR_METRICS_INTERVAL_MS", MetricsIntervalMs);
        MetricsIntervalNoClientsMs = ReadIntEnv("MONITOR_METRICS_INTERVAL_NOCLIENT_MS", MetricsIntervalNoClientsMs);
        HardwareIntervalMs = ReadIntEnv("MONITOR_HW_INTERVAL_MS", HardwareIntervalMs);
        AllowLocalNetworkCors = ReadBoolEnv("MONITOR_ALLOW_LOCAL_NETWORK_CORS", AllowLocalNetworkCors);
        AdaptiveUpdateNoClients = ReadBoolEnv("MONITOR_ADAPTIVE_NOCLIENTS", AdaptiveUpdateNoClients);
        CpuTempProvider = ReadStringEnv("CPU_TEMP_PROVIDER", CpuTempProvider);
        CpuTempFallbackToWmiApprox = ReadBoolEnv("CPU_TEMP_FALLBACK_TO_WMI_APPROX", CpuTempFallbackToWmiApprox);
    }

    public void Normalize()
    {
        if (Port <= 0)
        {
            Port = 8787;
        }

        if (MetricsIntervalMs <= 0)
        {
            MetricsIntervalMs = 1000;
        }

        if (MetricsIntervalNoClientsMs <= 0)
        {
            MetricsIntervalNoClientsMs = 2000;
        }

        if (HardwareIntervalMs <= 0)
        {
            HardwareIntervalMs = 2000;
        }

        var provider = string.IsNullOrWhiteSpace(CpuTempProvider)
            ? "lhm"
            : CpuTempProvider.Trim().ToLowerInvariant();
        CpuTempProvider = provider is "lhm" or "wmi" or "external" ? provider : "lhm";
    }

    private static int ReadIntEnv(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) ? value : fallback;
    }

    private static string ReadStringEnv(string name, string fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
    }

    private static bool ReadBoolEnv(string name, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record WsEnvelope<T>(string Type, T Data);
