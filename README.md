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
| Strg+Umschalt+F8 | Ansicht wechseln: Kompakt → Standard → Analyse |

Beenden über das Tray-Icon (Rechtsklick → Beenden).

**Ansichten** – alle stellen den Engpass in den Vordergrund, sind 400 px breit und haben eine feste Höhe (das Overlay
ändert seine Größe nur beim Umschalten), und überall steht unten die Hotkey-Zeile:
- **Kompakt** (Voreinstellung beim ersten Start): Urteil mit gemessener Anwendung, FPS und 1%-Low, Tipp, bis zu drei
  Engpass-Meldungen
- **Standard**: Urteil mit Anteilen der letzten 30 s, Beleg (z. B. „GPU wartet 2,7 ms · Hauptthread 97 %“) und Tipp,
  zwei Engpass-Meldungen, FPS mit Anwendung und Pacing, Belege als Balken (CPU-/GPU-Arbeitszeit pro Bild), eine
  Zeile „Spiel-Thread“ (Hauptthread des Spiels mit Namen, stärkster P-Kern) und eine Zeile Takt/Temperatur/Leistung
  beider Bauteile
- **Analyse**: oben identisch mit Standard, darunter abgesetzt die **Details**: Bild (Frametime-Verlauf der letzten
  5 s), Prozessor (Last, Takt, Kernbalken), Grafikkarte (VRAM, Speicher/Hotspot, Lüfter, PCIe, P-Status,
  GPU-Nutzer), System (RAM und Bestückung, Laufwerk, Datenquellen), Verbindung und weitere Hinweise

Gemessen wird die Anwendung im Vordergrund. Browser, KI-Apps (Claude, ChatGPT, Copilot …), Terminals, Launcher und
Hardware-Tools geben zwar auch Bilder aus, werden aber nicht bewertet – dann steht „KEIN SPIEL“ mit ihrem Namen da
(Liste in `Analysis\GameFilter.cs`, dieselbe gilt für die Aufzeichnung).

Meldungen sind in zwei Gruppen eingeteilt (`Analysis\NoteCategories.cs`): **Engpass** (Drosselung, Ruckler, VRAM/RAM
voll, Pacing, Fremdlast auf der GPU …) steht in allen Ansichten oben, **Hinweise** (HWiNFO, Hintergrundlast,
RAM-Konfiguration, PCIe, Verbindung …) nur in der Analyse unter „Weitere Hinweise“.

`PerfOverlay.exe --preview` rendert alle drei Ansichten mit künstlichen Daten – je eine CPU- und eine GPU-limitierte
Szene und eine ohne Spiel mit einer KI-App im Vordergrund – nach `preview-<ansicht>-<szene>.png`; `preview-groessen.txt` hält die Maße fest.

Ecke bzw. gezogene Position, Monitor, Ansicht und „Sitzungen aufzeichnen“ werden in
`%APPDATA%\PerfOverlay\einstellungen.json` gespeichert. Ist der gespeicherte Monitor nicht angeschlossen,
erscheint das Overlay auf dem Hauptmonitor; eine beschädigte Datei führt zu den Standardwerten.

Selbsttest ohne Fenster: `PerfOverlay.exe --selftest 6` schreibt `selftest.txt` und `selftest.png` neben die EXE. Die zweite Zeile nennt den Zeitgeber, z. B. „TSC (10 MHz, 12 ns pro Abfrage)“; als Administrator gestartet
zusätzlich, ob `useplatformclock` gesetzt ist. Die dritte Zeile zählt tote Verbindungen (CLOSE_WAIT) aller Programme, z. B. „3 (SearchApp 2, nvcontainer 1)“. Unter „=== System ===“ stehen Monitor, Energieplan, Speicher-Integrität, HAGS, Resizable BAR sowie die Grafikadapter, DPC-Anteil und Speicherzusage. Sitzungsdateien enthalten dieselbe Angabe als Kopfzeile `# Zeitgeber:`.

## Sitzungen aufzeichnen und auswerten

Sobald ein Spiel Frames liefert, schreibt das Overlay eine Sitzung nach `Dokumente\PerfOverlay\Sitzungen`
(eine CSV-Zeile pro Sekunde; Semikolon und Dezimalkomma, öffnet sich in deutschem Excel direkt). Nach 30 s ohne
Frames oder beim Spielwechsel beginnt eine neue Datei. Abschaltbar im Tray-Menü („Sitzungen aufzeichnen“).

`Auswertung\PerfOverlay-Auswertung.html` (Tray-Menü → „Auswertung öffnen“) wertet die Dateien per Drag & Drop
im Browser aus – ohne Internet, ohne Upload:
- **Ergebnis: Was hat gebremst?** ganz oben: Hauptengpass mit Anteil, Belege (wie viel der Bildzeit die GPU arbeitet,
  wie lange sie auf die CPU wartet, Hauptthread), „Was bringt mehr FPS?“ passend zum Engpass, bei Drosselung zuerst
  der Hinweis auf die Kühlung; darunter Arbeitszeit pro Bild mit Engpass-Streifen und Auslastung
- **Auf einen Blick**: Kernaussagen nach festen Schwellen – Engpass zuerst, dann Drosselung, Gleichmäßigkeit und
  Ruckler; Temperaturen ohne Drosselung und die Verbindung als „Nebenbefunde“. Als Hinweis formuliert, nicht als Diagnose
- Aufklappbare Abschnitte (Zustand wird im Browser gemerkt), jeder mit einer Lesehilfe „So liest du das“
- Fehlende Werte werden je Abschnitt mit Grund genannt: ältere Aufzeichnung, HWiNFO nicht aktiv,
  nur bei NVIDIA verfügbar, Spiel nicht auslesbar (Anti-Cheat), Ping-Messung aus
- Reihenfolge: Ergebnis, Auf einen Blick, Meldungen, Zusammenfassung, Bild und Ruckler, Prozessor, Grafikkarte,
  Speicher und Treiber, Verbindung (zugeklappt – sie betrifft den Lag, nicht die FPS)
- **Speicher und Treiber**: Speicher des Spiels, RAM-Belegung mit Speicherzusage, DPC-Anteil des höchsten Kerns
- **Leistung im Lauf der Sitzung** (ab 45 min): erstes gegen letztes Drittel (ohne die ersten 2 min). Sinken die FPS
  um mehr als 15 % oder verdoppeln sich die Ruckler, ist das eine Hauptaussage, wenn zugleich der Speicher des
  Spiels um mindestens 0,5 GB wuchs (Speicherleck, dann auch erster Tipp „Spiel neu starten“) oder die Temperaturen
  um mindestens 5 °C stiegen – sonst nur ein Nebenbefund, weil andere Spielszenen den Vergleich verfälschen können
- **Nebenbefunde** zusätzlich: Bufferbloat (Median-Ping in Sekunden mit Datenverkehr gegenüber ohne, ab 30 ms),
  Speicherzusage ab 90 %, Treiber-Unterbrechungen (mindestens 2 % der Zeit ≥ 20 % DPC)
- Zusammenfassung: Ø FPS, Ø 1%-Low, Hauptengpass, **Ø FPS im CPU-Limit**, Ruckler pro Minute, Drosselung;
  Temperaturen, Takt und Ping stehen in ihren Abschnitten
- Verlaufsdiagramme (FPS mit Engpass-Streifen und Rucklern, CPU-/GPU-Arbeitszeit, Auslastung, Temperaturen)
  mit synchronem Fadenkreuz. Bei langen Sitzungen je Pixelspalte zusammengefasst
  (Mittelwert als Linie, Minimum bis Maximum als Band); Zeitbereich mit der Maus aufziehen zoomt alle
  Diagramme synchron, Doppelklick zeigt wieder die ganze Sitzung
- Mehrere Dateien: Vergleichstabelle mit Abweichung zur ersten Sitzung, z. B. vor/nach XMP.
  „Ø FPS im CPU-Limit“ zeigt den Effekt von schnellerem RAM am deutlichsten. Darüber eine Einordnung je
  Sitzung: zuerst die Verschiebung des Engpasses (z. B. „CPU-Limit 70 % → 20 %“, Anteile in Prozentpunkten),
  dann besser/schlechter/gemischt, und ein Hinweis, wenn die Sitzungen aus verschiedenen Spielen stammen.

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
| AMD-GPU | ADLX (`amdadlx64.dll` aus dem Treiber): Last, Takt, Speichertakt, Temperatur, Hotspot, Speichertemperatur, Board-Leistung, Spannung, Lüfter (U/min), VRAM. Der erste Zugriff läuft in einem Probe-Prozess (`--adlx-probe`) – schlägt er fehl, bleibt ADLX abgeschaltet statt das Overlay abstürzen zu lassen. HWiNFO-Werte haben Vorrang. Drosselung: Hinweis ab 110 °C Hotspot (AMD meldet keine Gründe). |
| Andere GPUs / GPU-Last pro Prozess | PDH `GPU Engine(*engtype_3D)`, `GPU Adapter Memory` (herstellerunabhängig) |
| FPS, 1%-Low, CPU-/GPU-Busy pro Frame | Intel PresentMon 2.6 (Konsolenversion in `tools\`, signiert von Intel), live per STDOUT |
| RAM-Bestückung, Kanäle, Takt | WMI `Win32_PhysicalMemory` (SMBIOS Typ 17), einmal beim Start. Kanal aus Slot-Namen (`A2`, `DIMM_B1`, `ChannelA-DIMM0` …), sonst aus der Interleave-Tiefe. Nenntakt aus der Teilenummer (Corsair, G.Skill, Kingston, Crucial Ballistix, TeamGroup), sonst Hinweis bei JEDEC-Standardtakt. |
| CPU-Drosselung (immer) | PDH `Processor Information(_Total)\% Performance Limit` |
| Zeitgeber | Einmal beim Start: Frequenz von `QueryPerformanceCounter` (TSC meist 10 MHz, HPET 14,318 MHz, ACPI-Timer 3,58 MHz) und Kosten von 20.000 Abfragen (TSC etwa 10–50 ns, HPET mehrere hundert ns). Mit Admin-Rechten zusätzlich `bcdedit /enum {current}` → Startoption `useplatformclock`. Läuft ein Spiel mit langsamem Zeitgeber, meldet das Overlay einen Engpass mit Ursache (gesetzte Startoption bzw. Windows hält den TSC für ungeeignet). PerfOverlay ändert die Einstellung nie selbst. |
| Systemeinstellungen | Monitor des Vordergrundfensters: aktuelle gegenüber höchster Bildwiederholrate bei gleicher Auflösung (`EnumDisplaySettings`, alle 5 s); Energieplan (`PowerGetActiveScheme`, Energiemodus-Regler), Akku- und Stromsparmodus (`GetSystemPowerStatus`, alle 30 s); einmalig Speicher-Integrität (WMI `Win32_DeviceGuard`), HAGS (Registry `HwSchMode`) und bei NVIDIA Resizable BAR (BAR1-Größe aus NVML, 256 MB = aus). Hinweise bei Monitor ≥ 15 Hz unter seiner Möglichkeit, Akkubetrieb, Energiesparplan/Stromsparmodus und ReBAR aus; Speicher-Integrität und HAGS nur als Angabe in Selbsttest, Sitzungskopf und Auswertung. PerfOverlay ändert keine dieser Einstellungen. |
| Treiber-Unterbrechungen | PDH `\Processor Information(*)\% DPC Time` + `% Interrupt Time`, höchster Kern (Spalte `DPC_max_Proz`). Windows misst in Schritten eines Zeittakts (≈ 1,6 %), im Leerlauf kommen einzelne Sekunden bis etwa 12 % vor. Ab 20 % im Moment eines Rucklers lautet die Ursache „Treiber-Unterbrechung (DPC)“; 20 % für 5 s am Stück ergeben einen Hinweis. |
| Speicherzusage | Zugesicherter Speicher gegenüber der Grenze aus RAM und Auslagerungsdatei (`GlobalMemoryStatusEx`, Spalte `Commit_Proz`). Ab 90 % für 5 s Hinweis „Auslagerungsdatei zu klein“; ab 95 % im Moment eines Rucklers Ursache „Speicherzusage am Limit“. |
| VSync / G-Sync / FreeSync | Bildwiederholrate des Monitors gegenüber den FPS: ohne VSync dauerhaft über der Bildwiederholrate → Hinweis „Limit Hz − 3 FPS“; mit VSync je mindestens 15 % der Frames bei voller und halber Rate (z. B. 144 ↔ 72) → Hinweis „VSync springt“. |
| Bufferbloat | Einzelner Ping ins Internet, getrennt nach „Datenverkehr“ (≥ 3 MB/s Download oder ≥ 1 MB/s Upload) und „ruhig“; Median der letzten 30 s mit gegenüber 120 s ohne Verkehr. Ab 30 ms Anstieg Hinweis „QoS im Router“. |
| Speicher des Spiels | Privater Speicher des Spielprozesses (`GetProcessMemoryInfo`, eingeschränkter Lesezugriff, alle 5 s, Spalte `Spiel_RAM_GB`). Speicherleck: über 20 min mindestens 0,5 GB Wachstum (Ausgleichsgerade) und in den letzten 5 min weiter steigend; Laden neuer Gebiete erzeugt dagegen Stufen mit Plateau. |
| Grafikadapter des Spiels | 3D-Last des Spielprozesses je Adapter aus PDH `GPU Engine` (LUID im Instanznamen), Adapter über DXGI (`EnumAdapters1`). Intern = unter 1 GB eigener Grafikspeicher oder Intel ohne „Arc“. Liegt die meiste Last des Spiels (≥ 5 %) auf der Prozessorgrafik, obwohl eine Grafikkarte vorhanden ist, 5 s lang → Engpass „Spiel nutzt die Prozessorgrafik“. Optimus-Notebooks (Bild über die interne, Rendern auf der Grafikkarte) lösen das nicht aus. |
| Verbindung | Ping je Sekunde zum Router (Standard-Gateway) und zu `PingTarget` (Standard `1.1.1.1`, leer = aus) → Ø-Ping, Jitter, Verlust über 30 s; PDH `Network Interface(*)\Bytes Received/Sent`; Datendurchsatz je Prozess über `GetProcessIoCounters` (Datei + Netzwerk, daher „vermutlich“) |
| Tote Verbindungen | Je Sekunde die TCP-Tabelle über `GetExtendedTcpTable` (IPv4/IPv6, ohne Admin, wie `netstat -ano`), nur Verbindungen des Spielprozesses. Tot = CLOSE_WAIT: Die Gegenseite hat geschlossen, das Spiel nicht. Ab 3 Verbindungen, die länger als 30 s in CLOSE_WAIT hängen, Hinweis im Overlay (Fehler im Spiel, Neustart hilft vorübergehend). Spalten `Verbindungen` und `Tote_Verbindungen`; die Auswertung vergleicht Ruckler und FPS mit und ohne tote Verbindungen und stellt einen deutlichen Unterschied (doppelt so viele Ruckler oder 10 % weniger FPS) als Hauptaussage heraus. |
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

Verbindung (bremst keine FPS, verursacht aber Lag in Online-Spielen): Schwankt schon der Ping zum Router
(Verlust ≥ 3 %, Jitter ≥ 8 ms oder Ø ≥ 20 ms), liegt es am eigenen Netz – oft WLAN. Ist der Router stabil, das
Internet-Ziel aber nicht (Verlust ≥ 3 %, Jitter ≥ 15 ms oder Ø ≥ 100 ms), liegt es dahinter. Router, die Ping
grundsätzlich nicht beantworten, lösen keinen Hinweis aus. Zusätzlich ein Hinweis bei großem Down-/Upload
während des Spielens, mit dem vermutlichen Verursacher. Diese Meldungen sind Hinweise und erscheinen nur in der
Analyse-Ansicht – unter „Weitere Hinweise“ und im Abschnitt „Verbindung“.

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
| AMD-Grafikkarten (ADLX) | ⚠️ eingebaut, auf echter Hardware noch ungetestet |
| Intel-Grafikkarten | ⚠️ nur Auslastung und VRAM über Windows-Zähler, ungetestet |
| AMD-CPUs mit HWiNFO | ✅ Tctl/Tdie, Package Power, Drosselung über PPT/TDC/EDC-Limit (%) und HTC/PROCHOT, Power Reporting Deviation |
| AMD-CPUs ohne HWiNFO | ⚠️ Windows-„% Leistungslimit“ bleibt bei EDC-Begrenzung auf 100 %, Drosselung also nicht erkennbar |
| CPU-Temperaturbewertung | relativ zu TjMax (Intel: aus HWiNFO berechnet; AMD: nach Modell 89/90/95 °C, Hinweis nur bei Drosselung) |
| Mehrere Monitore | Overlay wechselt auf den Monitor, auf dem das Spiel im Vordergrund läuft |
| Hybrid-CPUs (Intel ab 12. Gen.) | ✅ P- und E-Kerne getrennt (Windows `GetSystemCpuSetInformation`, auch unter Windows 10); CPU-Schätzung nutzt nur P-Kerne |
| Viele Threads | Kernbalken skalieren ihre Breite, bis 64 Threads passen sie ins Overlay |
| Exklusiver Vollbildmodus | ❌ Overlay nicht sichtbar (nur randlos/Fenster) |

## Ideen für Phase 2

- Weitere Temperaturquellen neben HWiNFO: Core Temp, MSI Afterburner (Shared Memory)
- SSD-Temperatur über WMI `MSFT_StorageReliabilityCounter`
- Hauptfenster in der EXE mit Live-Auswertung (gleiche Datenbasis wie die CSV-Sitzungen)
- Download-Link im Kopf der Auswertung, sobald es einen Release Candidate gibt

## Lizenz

PerfOverlay steht unter der MIT-Lizenz, siehe [`LICENSE`](LICENSE).
Die mitgelieferte `tools\PresentMon.exe` stammt unverändert von Intel und steht unter deren MIT-Lizenz,
siehe [`tools\PresentMon-LICENSE.txt`](tools/PresentMon-LICENSE.txt).
