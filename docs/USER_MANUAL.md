# User Manual — Windows Monitoring Agent

## What this is
A lightweight Windows agent that shows realtime system status in a browser. It is designed for viewing from a tablet or another device on the same Wi‑Fi/LAN. It displays CPU/GPU usage, temperatures, and network traffic.

## Installation (setup)
1) Run `dist\installer\PCMonitoringAgentSetup.exe` as Administrator.
2) Follow the installer steps (default location is `Program Files`).
3) (Optional) enable:
   - **Allow LAN access** (opens firewall port 8787).
   - **Start agent on login** (auto‑start with a UAC prompt).
4) After installation you will get Desktop and Start Menu shortcuts.

## Run on the PC (step by step)
1) Double‑click the Desktop shortcut **PC Monitoring Agent**.
2) Accept the UAC prompt (the app requires Administrator privileges).
3) The agent runs in the background; the web panel is available at `http://localhost:8787`.

## Run from source (dev)
If you run from source, you need the .NET 8 SDK:

```powershell
dotnet run --project src/Agent/Agent.csproj
```

Keep the terminal window open while the agent is running.

## Open on a tablet (step by step)
1) Find the PC IP address:

```powershell
ipconfig
```

2) On the tablet, open:

```
http://192.168.1.50:8787
```

Replace the IP with the one from `ipconfig`.

## Metrics explained
- **CPU %**: current CPU load.
- **CPU temp (°C)**: CPU temperature.
- **GPU %**: current GPU load.
- **GPU temp (°C)**: GPU temperature.
- **Ethernet send/receive (kbps)**: network throughput in kilobits per second.
- **Mini charts (60s)**: last ~60 seconds of network activity.

## Warnings and thresholds
General guidelines (may vary by hardware):
- **CPU/GPU > 80°C** for a long time: check cooling.
- **CPU > 90%** consistently: expect slowdowns.
- **GPU > 90%** consistently: expect higher fan/temperature.

## FAQ
**Why do temperatures show `N/A`?**  
Some systems require Admin privileges or do not expose sensors. Run as Admin.

**Do I need WebView or extra components?**  
No. The UI is a standard web page in your browser.

**Does it work over Wi‑Fi?**  
Yes, as long as the PC and tablet are on the same network.

**Does the agent send data to the internet?**  
No. Data stays within the local network.

## Troubleshooting
- **Firewall blocks access**: use **Allow LAN access** during install or add a rule manually (Admin required):

```powershell
netsh advfirewall firewall add rule name="PC Monitoring Agent" dir=in action=allow protocol=TCP localport=8787
```

- **Temperatures are `N/A` or `Warming`**: wait ~10 seconds after start; run as Admin or verify sensor support on your hardware.
- **Need more CPU temp details**: open `http://localhost:8787/api/cpu-temp-debug` to see selected sensor and warm-up state.
- **GPU usage is `N/A`**: not supported on all GPUs/drivers.
- **Wrong network adapter**: the agent picks the most active one; disable other adapters or generate traffic on the desired one.
- **Cannot connect**: check the port and verify the IP address.

## CPU Temperature limitations on Windows
In some setups Windows can block vulnerable drivers (WinRing0 / Vulnerable Driver Blocklist / Core Isolation), which can prevent CPU temps via LibreHardwareMonitor.

Options:
- **LHM**: best accuracy when the driver is allowed.
- **External provider**: recommended if LHM is blocked (e.g., HWiNFO shared memory).
- **WMI ThermalZone**: approximate fallback only.

## Security
- The app does not send data to the internet.
- Use it only on a trusted LAN/Wi‑Fi network.
- Share access only on networks you trust.
