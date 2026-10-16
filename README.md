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
| Strg+Umschalt+F9 | Ecke wechseln (verwirft eine gezogene Position) |
| Strg+Umschalt **gedrückt halten** | Overlay mit der Maus verschieben (grüner Rahmen); losgelassen lässt es Klicks wieder durch |
| Strg+Umschalt+F8 | Detailzeilen ein/aus |

Beenden über das Tray-Icon (Rechtsklick → Beenden).

Ecke bzw. gezogene Position, Monitor, Detailmodus und „Sitzungen aufzeichnen“ werden in
`%APPDATA%\PerfOverlay\einstellungen.json` gespeichert. Ist der gespeicherte Monitor nicht angeschlossen,
erscheint das Overlay auf dem Hauptmonitor; eine beschädigte Datei führt zu den Standardwerten.

Selbsttest ohne Fenster: `PerfOverlay.exe --selftest 6` schreibt `selftest.txt` und `selftest.png` neben die EXE.

## Sitzungen aufzeichnen und auswerten

Sobald ein Spiel Frames liefert, schreibt das Overlay eine Sitzung nach `Dokumente\PerfOverlay\Sitzungen`
(eine CSV-Zeile pro Sekunde; Semikolon und Dezimalkomma, öffnet sich in deutschem Excel direkt). Nach 30 s ohne
Frames oder beim Spielwechsel beginnt eine neue Datei. Abschaltbar im Tray-Menü („Sitzungen aufzeichnen“).

`Auswertung\PerfOverlay-Auswertung.html` (Tray-Menü → „Auswertung öffnen“) wertet die Dateien per Drag & Drop
im Browser aus – ohne Internet, ohne Upload:
- Zusammenfassung: Ø FPS, Ø 1%-Low, Ruckler pro Minute, Hauptengpass, **Ø FPS im CPU-Limit**, Temperaturen
- Engpass-Anteile, Verlaufsdiagramme (FPS mit Engpass-Streifen und Rucklern, CPU-/GPU-Arbeitszeit,
  Auslastung, Temperaturen) mit synchronem Fadenkreuz
- Mehrere Dateien: Vergleichstabelle mit Abweichung zur ersten Sitzung, z. B. vor/nach XMP.
  „Ø FPS im CPU-Limit“ zeigt den Effekt von schnellerem RAM am deutlichsten.

`PerfOverlay.exe --preview` erzeugt neben der EXE zusätzlich zwei **künstliche** Beispielsitzungen
(`preview-sitzung-vorher.csv` / `-nachher.csv`) zum Ausprobieren der Auswertung.

Das Icon (`Assets\PerfOverlay.ico`) wird mit `Assets\make-icon.ps1` erzeugt.

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
| HWiNFO-Zustand | Beim Start und bei jedem Wechsel ein Hinweis, was fehlt: HWiNFO läuft nicht / läuft, aber Sensorfenster bzw. „Shared Memory Support“ fehlt / lieferte, hat aber aufgehört (beendet oder 12-h-Grenze der Gratisversion). |
| GPU-Spannung, Hotspot, Speicher-Junction (optional) | HWiNFO (`GPU Core Voltage`, `… Hot Spot …`, `… Memory Junction …`). Hinweise ab 100 °C Speichertemperatur (GDDR6X drosselt um 110 °C) und bei Hotspot ≥ 20 °C über GPU-Temperatur. |
| CPU-Temperatur, Package-Leistung, PL1, Drosselungsgründe (optional) | Shared Memory von **HWiNFO** (`Global\HWiNFO_SENS_SM2`), wird automatisch erkannt. In HWiNFO muss „Shared Memory Support“ aktiv sein (Gratisversion: 12 Stunden, danach neu aktivieren). |

## Engpass-Logik (`Analysis/BottleneckAnalyzer.cs`)

Mit Frame-Daten (zuverlässig), bezogen auf die vom Spiel gerenderten Frames (Frame Generation wird herausgerechnet):
- GPU-Busy ≥ 90 % der Frametime → **GPU-limitiert** (Normalfall)
- CPU wartet ≥ 25 % → **FPS-Limit** bzw. **VSync** (SyncInterval ≥ 1)
- GPU ≥ 15 % untätig („GPU wartet“) oder CPU-Busy ≥ 85 % → **CPU-limitiert**. „GPU wartet“ ist das
  zuverlässigere Signal, weil CPU-Busy bei manchen Engines auch das Warten auf die GPU enthält.
- sonst **gemischt**

Darstellung: Urteil mit konkretem Tipp, Anteile der Kategorien über die letzten 30 s (Balken), Frametime-Graph
der letzten 5 s mit markierten Rucklern, CPU-/GPU-Arbeitszeit pro Frame als Balken, Last des stärksten
Spiel-Threads („Hauptthread“, mit Thread-Name, falls die Engine einen setzt).

Ruckler und Frame-Pacing (`Sensors/FrameMetrics.cs`):
- **Ruckler** = ein Frame, der mindestens 1,8 × so lang ist wie der Median seiner ±10 Nachbarn **und** mindestens
  20 ms dauert. Der Vergleich mit den direkten Nachbarn sorgt dafür, dass Ladebildschirme oder Zwischensequenzen mit
  gleichmäßig niedrigen FPS nicht zählen (früher: Vergleich mit dem 10-s-Median → über 1000 Fehlzählungen in einer
  echten Warframe-Sitzung). 1,8 statt 2,0, damit ein doppelt angezeigtes Bild bei 60 FPS (33,3 ms) erkannt wird.
- **Frame-Pacing** = Ø Sprung der Frametime von Bild zu Bild (ohne Ruckler): unter 10 % der Frametime „ruhig“,
  ab 25 % „unruhig“ – erfasst Mikroruckeln, das aus vielen kleinen Schwankungen statt einzelner Hänger besteht.

Tests: `dotnet test tests\PerfOverlay.Tests` (Ruckler/Pacing, Engpass-Analyse mit echten Warframe-Fällen,
Sitzungsaufzeichnung inkl. Alt+Tab, RAM-Teilenummern).

Unerwartete Fehler landen in `Dokumente\PerfOverlay\fehler.log`; das Overlay läuft dabei weiter.

Zusätzliche Hinweise: GPU-Drosselung (Temperatur, Power-Brake), PCIe-Link reduziert, VRAM-Auslagerung in den RAM
(`GPU Process Memory\Shared Usage` des Spiels), RAM knapp, Datenträger ausgelastet, Ruckler mit Ursache
(Nachladen, CPU-Spitze, VRAM, unklar), Darstellung per Kopie (höhere Latenz), Frame Generation, Fremdlast auf GPU
und – bei CPU-Limit – Hintergrundprozesse mit hoher CPU-Last.

Ohne Frame-Daten wird geschätzt (GPU-Last und stärkster P-Kern). Das ist unsicher, weil der Scheduler einen
ausgelasteten Render-Thread auf mehrere Kerne verteilt.

Frame Generation wird über PresentMons `FrameType` erkannt. Das setzt voraus, dass Spiel oder Treiber generierte
Frames kennzeichnen – bei DLSS 3 ist das nicht garantiert (ungetestet).

`PerfOverlay.exe --preview` rendert das Overlay mit **künstlichen** Frame-Daten nach `preview.png`
(Kontrolle der Darstellung ohne Spiel).

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
- Hauptfenster in der EXE mit Live-Auswertung (gleiche Datenbasis wie die CSV-Sitzungen)
- Download-Link im Kopf der Auswertung, sobald es einen Release Candidate gibt
- Automatisch kompakter Modus (bei der Layout-Überarbeitung – ändert die Overlay-Größe)
- GPU-Nutzer im Leerlauf anzeigen (z. B. „wallpaper64 8 %, chrome 3 %“) – Daten liegen schon in
  `Snapshot.GpuTopProcesses` vor; erst bei der Layout-Überarbeitung einbauen, damit das Overlay nicht überladen wirkt
