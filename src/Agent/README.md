# Agent

Minimalni .NET 8 monitoring agent sa statičnim UI-jem i WebSocket stream-om.

## Pokretanje

```bash
dotnet run --project src/Agent/Agent.csproj
```

Podrazumevani port je `8787`, a servis se bind-uje na `0.0.0.0` kako bi bio
dostupan i na lokalnoj mreži.

## Konfiguracija

`src/Agent/appsettings.json`:

- `Port` (default `8787`)
- `MetricsIntervalMs` (default `1000`)
- `HardwareIntervalMs` (default `2000`)

Override preko env var:

- `MONITOR_PORT`
- `MONITOR_METRICS_INTERVAL_MS`
- `MONITOR_HW_INTERVAL_MS`

Opcioni CORS flag:

- `AllowLocalNetworkCors` (default `false`)
- `MONITOR_ALLOW_LOCAL_NETWORK_CORS=1` za enable

## Pristup sa tableta

1) Na računaru pronađi IP adresu:

```powershell
ipconfig
```

2) Na tabletu otvori:

```
http://<ip>:8787
```

## CORS (opciono)

Policy `AllowLocalNetwork` je definisan ali je default isključen. Kada je
uključen, dozvoljava origin-e iz lokalne mreže (localhost + private IP range).

## Windows Firewall (primer)

Primer komande (ne izvršavaj automatski, samo dokumentacija):

```powershell
netsh advfirewall firewall add rule name="PC Monitoring Agent" dir=in action=allow protocol=TCP localport=8787
```

## Napomena o privilegijama

Nema admin zahteva osim ako čitanje hardware temperatura to ne zahteva na
konkretnoj konfiguraciji.

## Test senzora (CPU/GPU)
- Pokreni app kao Administrator ako je potrebno.
- Otvori `/api/sensors` i proveri da li postoji CPU temperature senzor.
