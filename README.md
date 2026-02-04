# PC Monitoring Agent

Lightweight Windows monitoring agent with a real‑time web dashboard. Designed for a second‑screen view on a tablet or another device on the same LAN.

![Image](https://github.com/user-attachments/assets/13480a92-94a0-42ea-88e7-f8b2d27e9831)

## Highlights
- Realtime CPU/GPU/NET/RAM metrics over WebSocket
- Dark, static UI with zero frontend dependencies
- Low overhead and configurable sampling
- LAN‑friendly access (tablet, phone, or another PC)
- Self‑contained single‑file release + Windows installer

## Ideal for
- Home lab and gaming rigs
- Kiosk or workstation monitoring
- Quick, shareable status view without heavy tooling

## Tech stack
- .NET 8 minimal API (Kestrel)
- Windows Performance Counters (CPU, network)
- LibreHardwareMonitor (CPU/GPU temp + GPU usage)
- HTML/CSS/JS UI in `wwwroot`

## Requirements
- Windows 10/11
- .NET 8 SDK (only for building from source)
- Admin privileges required to read some hardware sensors

## Install (end users)
The installer is produced after a build and is located at:

`dist\installer\PCMonitoringAgentSetup.exe`

Run it as Administrator and follow the wizard. You can enable:
- **Allow LAN access** (opens firewall port 8787)
- **Start agent on login** (autostart with UAC prompt)

After install, use the Desktop shortcut and open:

`http://localhost:8787` or `http://<ip>:8787`

## Developer quick start

```powershell
dotnet run --project src/Agent/Agent.csproj
```

Then open:

```
http://localhost:8787
```

## Release build (single‑file)
Creates a self‑contained, single‑file executable:

```powershell
.\build\publish.ps1
```

Output: `dist\win-x64`

## Build the installer
Requires Inno Setup (ensure `ISCC.exe` is in PATH or installed in the default location).

```powershell
.\installer\build-installer.ps1
```

Output: `dist\installer\PCMonitoringAgentSetup.exe`

## Configuration
Settings are in `src/Agent/appsettings.json` and can be overridden by env vars:

- `MONITOR_PORT` (default `8787`)
- `MONITOR_METRICS_INTERVAL_MS` (default `1000`)
- `MONITOR_METRICS_INTERVAL_NOCLIENT_MS` (default `2000`)
- `MONITOR_HW_INTERVAL_MS` (default `2000`)
- `MONITOR_ALLOW_LOCAL_NETWORK_CORS` (default `false`)
- `MONITOR_ADAPTIVE_NOCLIENTS` (default `false`)
- `CPU_TEMP_PROVIDER` (default `lhm`, options: `lhm|wmi|external`)

Example:

```powershell
$env:MONITOR_PORT="9000"
dotnet run --project src/Agent/Agent.csproj
```

## API reference
Base URL: `http://<host>:<port>`

**GET `/api/health`**

```json
{
  "ok": true,
  "time": "2026-02-02T12:34:56.789Z"
}
```

**GET `/api/metrics`**

```json
{
  "cpuPercent": 12,
  "cpuTempC": 54.2,
  "cpuTempSource": "CPU Package",
  "gpuUsagePercent": 23.5,
  "gpuTempC": 61.0,
  "netSendKbps": 120,
  "netReceiveKbps": 980,
  "errors": []
}
```

**GET `/api/sensors`** (debug)

```json
{
  "ok": true,
  "sensors": [
    {
      "hardwareName": "Intel CPU",
      "hardwareType": "Cpu",
      "sensorName": "CPU Package",
      "sensorType": "Temperature",
      "value": 54.2,
      "identifier": "/some/identifier"
    }
  ]
}
```

**GET `/api/cpu-temp-debug`** (debug)

```json
{
  "ok": true,
  "cpuTemp": {
    "tempC": 54.2,
    "source": "CPU Package",
    "status": "ok",
    "provider": "lhm",
    "details": {
      "cpuTempSensorsFound": 15,
      "cpuTempSensorsWithValue": 10,
      "selectedSensorName": "CPU Package"
    }
  }
}
```

**WebSocket `/ws`**
- URL: `ws://<host>:<port>/ws`
- Message format: `{ "type": "...", "data": { ... } }`
- Types:
  - `init` (includes `series`)
  - `metrics` (no series)
  - `series` (periodic with series)

## Troubleshooting
- **Temps show `N/A` or `Warming`**: wait ~10 seconds after start; run as Admin or check sensor support on your hardware.
- **CPU temp still missing**: use `/api/sensors` to verify temperature sensors exist.
- **GPU usage is `N/A`**: not supported on all GPUs/drivers.
- **Cannot connect from tablet**: open firewall port 8787 or enable **Allow LAN access** in installer.

## CPU Temperature limitations on Windows
- Windows may block vulnerable drivers (WinRing0 / Vulnerable Driver Blocklist / Core Isolation), so CPU temps can be unavailable in LHM.
- Options:
  - **LHM**: best accuracy when the driver is allowed.
  - **External provider**: recommended if LHM is blocked (e.g., HWiNFO shared memory).
  - **WMI ThermalZone**: approximate fallback only.

## Roadmap
- Tray app and elevated autostart
- MSI packaging option
- Adapter picker and LAN discovery

## License
TBD

