# CPU Temperature – Debug Review

## Kontekst
- Projekat: Windows monitoring agent (.NET 8, LibreHardwareMonitorLib 0.9.5).
- CPU model (iz `sensors.json`): **Intel Core i5‑12400**.
- LibreHardwareMonitor UI (slika) pokazuje validne CPU temperature (Package/Core).
- Naš `/api/metrics` vraća `cpuTempC = null`, a UI prikazuje `N/A`.

## Ključni simptomi
- CPU temperature senzori postoje (npr. `CPU Package`, `Core Max`, `CPU Core #1..#6`), ali **nemaju vrednosti** (`hasValue=false`, bez `rawValue`).
- WMI Thermal Zone vraća vrednosti (npr. **27.8°C**), ali je očigledno netačno / statično.

## Evidencija (poslednji setovi podataka)
- `sensors (4).json`:
  - CPU temperature senzori: `hasValue=false` za sve `Temperature` senzore.
  - WMI Thermal Zone: `TZ00_0 = 27.8°C`, `TZ10_0 = 16.8°C`.
- `metrics.json`:
  - `cpuTempC` nema vrednost (`null`), `cpuTempSource` ostaje `"CPU Package"`.

## Šta je implementirano i pokušano

### 1) Robustna selekcija CPU temp senzora
**Fajl:** `src/Agent/Services/HardwareMonitorService.cs`
- Skeniranje CPU + Motherboard uređaja.
- Prioritet: Package → Tctl/Tdie → Core Max/CCD → Core → hottest fallback.
- Validacija vrednosti: `null`, `NaN`, `<0`, `>120` ignoriše se.
- Cache + rescan na 30s i na 5 uzastopnih invalid tick‑ova.

### 2) Redovan update hardvera
- `computer.Open()` na startu.
- Update na svakom tick‑u.
- Dodato `UpdateVisitor` za `computer.Accept(visitor)`.
- Dodat **dupli update** za CPU/Motherboard.

### 3) Debug endpoint `/api/sensors`
- Vraća listu CPU/GPU senzora sa `hardwareName`, `hardwareType`, `sensorName`, `sensorType`, `value`, `identifier`.
- Dodata polja **`hasValue`** i **`rawValue`** da se vidi da li LibreHardwareMonitor uopšte isporučuje vrednost.

### 4) WMI fallback za CPU temperaturu
- Dodate WMI klase:
  - `root\WMI: MSAcpi_ThermalZoneTemperature`
  - `root\CIMV2: Win32_PerfFormattedData_Counters_ThermalZoneInformation`
  - `root\CIMV2: Win32_TemperatureProbe`
- Zaključak: WMI vraća statične/netečne vrednosti (npr. 27.8°C), pa je fallback ograničen.
- Trenutno se WMI **koristi samo ako CPU senzori ne postoje**, da se izbegne pogrešna temperatura kada senzori postoje, ali ne daju vrednost.

### 5) Aktivacija senzora (pokušaji)
- Dodata refleksija za aktivaciju senzora:
  - Properties: `IsActive`, `Active`
  - Methods: `Activate()`, `SetActive(true)`
  - Fields: `active`, `_active`, `isActive`, `_isActive`
- Efekat: CPU temperature senzori i dalje nemaju `value`.

### 6) UI i snapshot promene
- `MetricsSnapshot` dobio `cpuTempSource`.
- UI prikazuje `cpuTempC` i opcioni `source: ...`.
- `/api/metrics` nosi `cpuTempC` + `cpuTempSource`.

## Zaključak (trenutno stanje)
- LibreHardwareMonitor **pronalazi CPU temperature senzore**, ali **ne isporučuje vrednosti** (`hasValue=false`) na ovoj mašini.
- WMI temperature postoje, ali su **nepouzdane** (npr. 27.8°C statično).
- LibreHardwareMonitor UI prikazuje realne vrednosti, što sugeriše:
  - UI može koristiti dodatni driver ili drugi backend,
  - ili je potrebna specifična konfiguracija koja nije aktivirana u `LibreHardwareMonitorLib`.

## Predlozi za sledeće korake
1) **Proveriti LHM UI logiku/driver**:
   - Da li UI koristi kernel driver (Ring0) koji nije dostupan u Lib načinu?
   - Proveriti Windows **Core Isolation / Memory Integrity** (poznato da blokira driver).
2) **Probati noviju verziju LibreHardwareMonitorLib**.
3) **Uvesti alternativni izvor**:
   - HWiNFO Shared Memory,
   - OpenHardwareMonitor WMI provider,
   - OEM utility (ako postoji za ploču).
4) **Opcioni fallback**:
   - dozvoliti WMI temp uz jasno upozorenje da je “approx/thermal zone”.

## Relevantni fajlovi koji su menjani
- `src/Agent/Services/HardwareMonitorService.cs`
- `src/Agent/Services/MetricsCollector.cs`
- `src/Agent/Services/MetricsSnapshot.cs`
- `src/Agent/Program.cs`
- `src/Agent/wwwroot/app.js`
- `src/Agent/wwwroot/index.html`
- `src/Agent/README.md`
- `README.md`
