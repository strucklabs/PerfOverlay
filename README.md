# PerfOverlay

Gaming-Overlay für Windows (C# / .NET 10 / WPF), das Leistungswerte anzeigt und den aktuellen Engpass bestimmt.
Es läuft komplett im User Mode, ohne eigenen Kernel-Treiber (kein WinRing0, keine Probleme mit HVCI oder Anti-Cheat).

## Starten

```
dotnet build -c Release
bin\Release\net10.0-windows\PerfOverlay.exe
```

**Für FPS und Frame-Analyse als Administrator starten** (Rechtsklick → „Als Administrator ausführen“ oder über das Tray-Menü).
PresentMon nutzt ETW. Dafür braucht es Admin-Rechte oder die Mitgliedschaft in der Gruppe „Leistungsprotokollbenutzer“.

Das Overlay ist nur über **randlosen Fenstermodus / Fenstermodus** sichtbar, nicht über exklusivem Vollbild.

| Hotkey | Funktion |
|---|---|
| Strg+Umschalt+F10 | Overlay ein/aus |
| Strg+Umschalt+F9 | Ecke wechseln |
| Strg+Umschalt+F8 | Detailzeilen ein/aus |

Beenden über das Tray-Icon (Rechtsklick → Beenden).

Selbsttest ohne Fenster: `PerfOverlay.exe --selftest 6` schreibt `selftest.txt` und `selftest.png` neben die EXE.

## Datenquellen

| Bereich | Quelle |
|---|---|
| CPU-Last pro Kern, effektiver Takt | PDH: `Processor Information` (`% Processor Time`, `% Processor Performance` × `Processor Frequency`) |
| RAM, Commit, Nachladen von der Platte | `GlobalMemoryStatusEx`, PDH `Memory\Pages Input/sec` |
| Datenträger | PDH `PhysicalDisk(*)` – Aktivität und Latenz des am stärksten belasteten Laufwerks |
| NVIDIA-GPU | NVML (`nvml.dll` aus dem Treiber): Last, Takt, Temperatur, Leistung/Limit, VRAM, Lüfter, PCIe, Throttle-Reasons |
| Andere GPUs / GPU-Last pro Prozess | PDH `GPU Engine(*engtype_3D)`, `GPU Adapter Memory` (herstellerunabhängig) |
| FPS, 1%-Low, CPU-/GPU-Busy pro Frame | Intel PresentMon 2.6 (Konsolenversion in `tools\`, signiert von Intel), live per STDOUT |
| RAM-Bestückung, Kanäle, Takt | WMI `Win32_PhysicalMemory` (SMBIOS Typ 17), einmal beim Start. Kanal aus Slot-Namen (`A2`, `DIMM_B1`, `ChannelA-DIMM0` …), sonst aus der Interleave-Tiefe. Nenntakt aus der Teilenummer (Corsair, G.Skill, Kingston, Crucial Ballistix, TeamGroup), sonst Hinweis bei JEDEC-Standardtakt. |
| CPU-Drosselung (immer) | PDH `Processor Information(_Total)\% Performance Limit` |
| CPU-Temperatur, Package-Leistung, PL1, Drosselungsgründe (optional) | Shared Memory von **HWiNFO** (`Global\HWiNFO_SENS_SM2`), wird automatisch erkannt. In HWiNFO muss „Shared Memory Support“ aktiv sein (Gratisversion: 12 Stunden, danach neu aktivieren). |

## Engpass-Logik (`Analysis/BottleneckAnalyzer.cs`)

Mit Frame-Daten (zuverlässig):
- GPU-Busy ≥ 90 % der Frametime → **GPU-limitiert** (Normalfall)
- CPU-Busy ≥ 85 % und GPU-Busy < 80 % → **CPU-limitiert** (GPU wartet)
- CPU wartet ≥ 25 % der Frametime → **FPS-Limit / VSync**
- sonst **gemischt**

Zusätzliche Hinweise: GPU-Drosselung (Temperatur, Power-Brake), PCIe-Link reduziert, VRAM voll, RAM knapp / Auslagerung,
Datenträger ausgelastet, Ruckler (99. Perzentil > 2,5 × Median), Fremdlast auf der GPU.

Ohne Frame-Daten wird geschätzt (GPU-Last und stärkster Kern). Das ist unsicher, weil der Scheduler einen
ausgelasteten Render-Thread auf mehrere Kerne verteilt.

## Kompatibilität

| Bereich | Status |
|---|---|
| Windows 10 22H2, Intel Core i5-13600KF, RTX 3080, 2 Monitore | ✅ getestet |
| Windows 11 25H2, AMD Ryzen 9 5900XT (32 Threads), RTX 4070 SUPER | ✅ Selbsttest (Leerlauf + Last) |
| Beliebige Kernzahl, beliebige Windows-Sprache | ✅ (Kerne zur Laufzeit ermittelt, englische PDH-Namen; 32 Threads passen) |
| NVIDIA-Karten (NVML) | ✅ |
| AMD-/Intel-Grafikkarten | ⚠️ nur Auslastung und VRAM über Windows-Zähler, ungetestet |
| AMD-CPUs mit HWiNFO | ✅ Tctl/Tdie, Package Power, Drosselung über PPT/TDC/EDC-Limit (%) und HTC/PROCHOT, Power Reporting Deviation |
| AMD-CPUs ohne HWiNFO | ⚠️ Windows-„% Leistungslimit“ bleibt bei EDC-Begrenzung auf 100 %, Drosselung also nicht erkennbar |
| CPU-Temperaturbewertung | relativ zu TjMax (Intel: aus HWiNFO berechnet; AMD: nach Modell 89/90/95 °C, Hinweis nur bei Drosselung) |
| Mehrere Monitore | Overlay wechselt auf den Monitor, auf dem das Spiel im Vordergrund läuft |
| Hybrid-CPUs (Intel ab 12. Gen.) | ✅ P- und E-Kerne getrennt (Windows `GetSystemCpuSetInformation`, auch unter Windows 10); CPU-Schätzung nutzt nur P-Kerne |
| Viele Threads | Kernbalken skalieren ihre Breite, bis 64 Threads passen sie ins Overlay |
| Exklusiver Vollbildmodus | ❌ Overlay nicht sichtbar (nur randlos/Fenster) |

## Ideen für Phase 2

- Weitere Temperaturquellen neben HWiNFO: Core Temp, MSI Afterburner (Shared Memory)
- AMD-spezifische Werte über ADLX
- SSD-Temperatur über WMI `MSFT_StorageReliabilityCounter`
- P-/E-Kerne getrennt darstellen (`GetSystemCpuSetInformation`, EfficiencyClass)
- CSV-Logging und Einstellungen
- GPU-Nutzer im Leerlauf anzeigen (z. B. „wallpaper64 8 %, chrome 3 %“) – Daten liegen schon in
  `Snapshot.GpuTopProcesses` vor; erst bei der Layout-Überarbeitung einbauen, damit das Overlay nicht überladen wirkt
