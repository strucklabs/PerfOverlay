# Plan: Sensoren ohne HWiNFO (Stand 11.11.2026)

Umgesetzt am 11.11.2026 in S1–S6 (5861f2c, f9a30d9, 5c9cb9a, 639968b, faf3421, Version 0.5.0). Abnahme: 1, 3 (ohne HWiNFO-Vergleich), 6, 7, 8 auf Marcos PC geprüft; offen: 2 (Vergleich mit laufendem HWiNFO), 4 (PawnIO nachträglich ohne Neustart, nur im Code), 5 (AMD nur mit Testwerten).

## Ziel

PerfOverlay soll nicht mehr auf HWiNFO angewiesen sein:

- **GPU:** alle Werte selbst auslesen.
- **CPU:** nehmen, was gerade da ist. Das ist zuerst PawnIO (falls installiert), dann HWiNFO (falls es läuft).
  Gibt es beides nicht, nennt ein Hinweis beide Möglichkeiten.

## Grundsätze

- PerfOverlay installiert nichts und ändert keine Systemeinstellungen. PawnIO wird nur erkannt und genutzt,
  im Hinweis steht der Link pawnio.eu.
- Ohne Admin-Rechte funktioniert alles, was ohne Admin geht. Das sind alle GPU-Werte. CPU-Werte über PawnIO
  brauchen Admin, wie die FPS-Messung.
- Es wird nichts entfernt, was heute funktioniert. HWiNFO bleibt als Quelle für die CPU erhalten.
- Texte sind neutral und einsteigerfreundlich.

## Quellen je Messwert (Zielbild)

| Messwert | NVIDIA | AMD | CPU / Mainboard |
|---|---|---|---|
| Last, Takt, Temperatur, Leistung, Lüfter % | NVML (wie bisher) | ADLX (wie bisher) | – |
| GPU-Lüfter U/min | **neu:** NVML `nvmlDeviceGetFanSpeedRPM` | ADLX (wie bisher) | – |
| Hotspot, Speichertemperatur, Spannung | **neu:** NVAPI (über LibreHardwareMonitorLib, siehe S2) | ADLX (wie bisher) | – |
| CPU-Temperatur, CPU-Leistung | – | – | 1. PawnIO · 2. HWiNFO · 3. nichts |
| CPU- und Mainboard-Lüfter | – | – | 1. PawnIO · 2. HWiNFO · 3. nichts |
| CPU-Drosselung | – | – | immer: Windows-Zähler „% Performance Limit“ (wie bisher); Grund aus HWiNFO **oder** abgeleitet (siehe S4) |

## Blöcke

### S1 – NVML: GPU-Lüfter in U/min
- `nvmlDeviceGetFanSpeedRPM` (Struktur mit Version und Lüfterindex), alle Lüfter, angezeigt wird der Mittelwert.
- Ältere Treiber haben die Funktion nicht, dann bleibt der Wert leer (EntryPointNotFound abfangen).
- Ersetzt `GpuThermal.FanRpm` aus HWiNFO.

### S2 – LibreHardwareMonitorLib einbinden (NuGet), zunächst nur GPU
- Entscheidung: kein eigener NVAPI-Code. LibreHardwareMonitorLib liest Hotspot, Speichertemperatur und Spannung
  über NVAPI und wird bei neuen Treibern und Karten gepflegt. Für die GPU braucht die Bibliothek keinen Treiber.
- Nur der GPU-Teil wird eingeschaltet (`IsGpuEnabled`). Der CPU-Teil kommt erst in S3, nur mit PawnIO.
- Lesen im Hintergrund (eigener Takt, 1 s), damit das Overlay nicht wartet.
- Zusammenführen wie heute mit ADLX: Eigene Werte haben Vorrang, HWiNFO füllt nur noch CPU-Lücken.
- Lizenz: MPL 2.0, Bibliothek unverändert. Lizenztext liegt im Paket wie bei PresentMon, dazu ein Absatz in der README.

### S3 – PawnIO erkennen und für die CPU nutzen
- Erkennung: Ist der Treiber-Dienst „PawnIO“ eingerichtet und läuft PerfOverlay als Admin, wird der CPU-Teil
  der Bibliothek eingeschaltet (`IsCpuEnabled`, `IsMotherboardEnabled` für den Lüfterchip).
- Geprüft wird beim Start und danach alle 30 s, damit ein nachträglich installiertes PawnIO ohne Neustart greift.
- Werte: CPU-Package-Temperatur (bzw. Tctl/Tdie bei AMD), CPU-Package-Leistung, Lüfter des Mainboards.
  Der CPU-Lüfter ist der Lüfter mit „CPU“ im Namen, wie heute bei HWiNFO.
- Hinweis (Info, Analyse-Ansicht): „CPU-Werte über PawnIO“. Er dient zur Einordnung, warnt aber vor nichts.
- Läuft HWiNFO gleichzeitig, hat PawnIO Vorrang. Die Quelle soll eindeutig sein und Werte nicht springen.

### S4 – Drosselgrund ohne HWiNFO
- Heute liefert HWiNFO die Gründe fertig („Temperatur“, „Stromlimit PL1“ …). Über PawnIO gibt es nur
  Temperatur und Leistung, deshalb wird der Grund aus „% Performance Limit“ < 100 abgeleitet:
  - Temperatur ≥ Maximaltemperatur − 5 °C → „Temperatur“
  - Leistung ≥ 95 % des Leistungslimits (falls die Bibliothek es liefert), sonst ohne Grund → „Strom-/Leistungslimit“
  - sonst: Meldung wie heute ohne Grund („CPU begrenzt auf … %“)
- Die Maximaltemperatur (TjMax) liefert die Bibliothek bei Intel, bei AMD gilt sie wie heute als Normalbetrieb.

### S5 – Hinweise, Selbsttest, Auswertung
- Den Hinweis „Mehr Werte … mit HWiNFO“ ersetzt ein Hinweis nur zur CPU, und nur wenn weder PawnIO noch HWiNFO
  Werte liefern: „CPU-Temperatur: HWiNFO starten oder PawnIO installieren (pawnio.eu)“.
- Die HWiNFO-Statushinweise („Shared Memory aktivieren“, „12-h-Grenze“) bleiben, aber nur, wenn HWiNFO läuft
  und PawnIO nicht genutzt wird.
- Selbsttest: Zeile „CPU-Sensoren: PawnIO / HWiNFO / keine“ und „GPU-Sensoren: NVML + NVAPI / ADLX“.
  Die Rohdaten-Ausgabe der Bibliothek kommt dazu, wie heute bei HWiNFO.
- Sitzungskopf: `# CPU-Sensoren: …`.
- Auswertung: Die Gründe für fehlende Werte werden angepasst. GPU-Werte nennen HWiNFO nicht mehr als Grund,
  CPU-Werte nennen „HWiNFO oder PawnIO“. Ältere Sitzungen behalten ihre bisherigen Gründe.

### S6 – README, LIESMICH, Testpaket
- README: Quellen-Tabelle und Abschnitt HWiNFO überarbeiten, PawnIO erklären (was es ist, dass PerfOverlay es nicht
  installiert, Hinweis auf Anti-Cheat).
- LIESMICH: Abschnitt „MEHR MESSWERTE MIT HWiNFO“ wird zu „CPU-TEMPERATUR UND LÜFTER“ mit beiden Wegen.
- Testpaket: Version 0.5.0, die Bibliothek und ihr Lizenztext liegen im Paket.

Nach jedem Block: Tests grün, Vorschau prüfen, dann Rückfrage zum Commit.

## Abnahmekriterien

1. **Ohne HWiNFO und ohne PawnIO** (Marcos PC heute): Der Selbsttest zeigt GPU-Lüfter in U/min, Hotspot,
   Speichertemperatur und Spannung der RTX 3080. Der einzige Sensor-Hinweis ist der zur CPU.
2. **Mit HWiNFO, ohne PawnIO:** CPU-Temperatur, CPU-Leistung und Drosselgründe wie bisher. Die GPU-Werte kommen aus
   der eigenen Abfrage. Die Werte weichen höchstens 2 °C bzw. 2 W von HWiNFO ab.
3. **Mit PawnIO** (Probeinstallation auf Marcos PC, nur wenn er zustimmt): CPU-Temperatur und -Leistung ±2 °C/±3 W wie
   HWiNFO. Der CPU-Lüfter erscheint, der Hinweis „CPU-Werte über PawnIO“ ist da. Ohne Admin-Rechte fehlen die
   CPU-Werte, Fehlermeldungen gibt es keine.
4. **PawnIO nachträglich installiert:** Die CPU-Werte erscheinen spätestens 30 s später, ohne Neustart.
5. **AMD-Grafikkarte:** unverändert über ADLX (Tests mit nachgestellten Werten, keine echte Hardware vorhanden).
6. **Leistung:** Die zusätzlichen Abfragen kosten im Messtakt unter 2 ms. Die Bibliothek liest im Hintergrund.
7. **Alte Sitzungen** öffnen sich in der Auswertung ohne Fehler und mit den bisherigen Gründen für fehlende Werte.
8. Alle bisherigen Tests bleiben grün, neue Tests decken Quellenwahl, Vorrang, Drosselgrund und Hinweistexte ab.

## Zu beachten (Prüfung auf Widersprüche)

- **Admin:** PawnIO funktioniert nur mit Admin-Rechten. Ohne Admin darf PerfOverlay deshalb nicht „PawnIO installieren“
  raten, wenn PawnIO schon installiert ist. Dann lautet der Hinweis „Für CPU-Werte als Admin starten“.
- **Doppelte Quellen:** HWiNFO und die Bibliothek dürfen nicht gleichzeitig dieselben GPU-Werte liefern. Es gilt immer
  die eigene Abfrage, HWiNFO liefert nur noch die CPU-Werte.
- **Anti-Cheat:** PawnIO ist installiert oder nicht, PerfOverlay ändert daran nichts. Die README weist darauf hin, dass
  manche Anti-Cheat-Systeme empfindlich auf Treiber für Hardwarezugriff reagieren.
- **Afterburner/RTSS:** NVAPI-Abfragen laufen parallel zu Afterburner problemlos (nur lesend).
- **RTX 50:** Der Hotspot fehlt dort über NVAPI. Er wird einfach nicht angezeigt, ein Hinweis wäre irreführend.
- **Größe:** Die Bibliothek vergrößert das Paket (Schätzung einige MB). Ob das akzeptabel ist, zeigt der Build in S2.
- **Hybrid-Intel:** Die Bibliothek liefert die Temperatur je Kern und für das Package. Angezeigt wird das Package,
  wie heute bei HWiNFO.
