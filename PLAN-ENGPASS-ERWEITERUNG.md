# Plan: weitere Engpass-Gründe (Stand 08.11.2026)

Nur Planung, noch nichts umgesetzt. Grundlage ist die Zusammenstellung der im Netz genannten Engpass-Gründe
(Abgleich mit den vorhandenen Meldungen). Ausgewählt sind 12 Kandidaten: die 8 aus der Zusammenstellung
plus HAGS, falsch eingestelltes G-Sync/FreeSync, Speicherleck im Spiel und Bufferbloat. Dazu Ergänzungen
der Tipps (Abschnitt T).

Grundsätze wie bisher:

- PerfOverlay liest nur und ändert nie selbst Einstellungen (Energieplan, VBS, BIOS …).
- Texte sind neutral und einsteigerfreundlich.
- „Engpass“ heißt, der Grund kostet nachweislich FPS oder macht Ruckler. Alles andere ist ein „Hinweis“ und
  steht in der Analyse-Ansicht. Reine Systemangaben sind „Info“: Sie stehen im Selbsttest, im Sitzungskopf und
  im Kopf der Auswertung, aber nicht als Meldung.
- Alles funktioniert ohne Admin-Rechte.

## Kandidaten

| Nr. | Grund | Messung | Einordnung |
|---|---|---|---|
| K1 | Monitor läuft unter seiner möglichen Bildwiederholrate (z. B. 60 statt 144 Hz) | `EnumDisplaySettings`: aktuelle Hz gegenüber der höchsten Hz bei gleicher Auflösung, für den Monitor des Spielfensters | Hinweis; beim Urteil VSYNC an dieser Grenze wird der Tipp angepasst (T1) |
| K2 | Energiesparplan „Energiesparmodus“ bzw. Notebook im Akkubetrieb | `PowerGetActiveScheme` plus Windows-11-Energiemodus (`PowerGetEffectiveOverlayScheme`), `GetSystemPowerStatus` | Hinweis; „Ausbalanciert“ nur als Info und als Tipp beim CPU-Limit (T3) |
| K3 | Speicher-Integrität (VBS/HVCI) aktiv | WMI `Win32_DeviceGuard` → `SecurityServicesRunning` | Info; beim CPU-Limit ein Tipp in der Auswertung, der den Sicherheitsnachteil ausdrücklich nennt (T3) |
| K4 | Treiber blockieren die CPU (DPC-/Interrupt-Zeit) | PDH `\Processor(*)\% DPC Time` und `% Interrupt Time` je Kern | Neue Ruckler-Ursache „Treiber-Unterbrechung“; Hinweis, wenn dauerhaft hoch |
| K5 | Zugesicherter Speicher am Limit (Auslagerungsdatei zu klein) | `CommitGB/CommitLimitGB` (wird schon gemessen) | Hinweis ab 90 %; Engpass ab 95 % zusammen mit Rucklern |
| K6 | Leistung sinkt im Lauf der Sitzung | Nur in der Auswertung: FPS und Ruckler im ersten gegenüber dem letzten Drittel, ab 45 min Sitzungslänge | Nebenbefund; Hauptaussage nur zusammen mit K11 oder steigenden Temperaturen |
| K7 | Resizable BAR aus (NVIDIA) | NVML `nvmlDeviceGetBAR1MemoryInfo`: BAR1 bei etwa 256 MB trotz mehr VRAM | Info und Hinweis; NVIDIA nutzt ReBAR nur in freigegebenen Spielen, deshalb kein Engpass |
| K8 | Spiel läuft auf der internen statt der dedizierten Grafik | PDH `GPU Engine`: 3D-Last des Spielprozesses je Adapter (LUID), Adapternamen über DXGI | Engpass, wenn eine dedizierte GPU vorhanden ist |
| K9 | HAGS (hardwarebeschleunigte GPU-Planung) | Registry `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode` | Nur Info (Selbsttest, Sitzungskopf, Kopf der Auswertung) |
| K10 | G-Sync/FreeSync falsch eingestellt | Muster aus Hz (K1), FPS, `SyncInterval`, `AllowsTearing`: (a) FPS ohne VSync dauerhaft über der Bildwiederholrate, (b) VSync an und FPS springen zwischen Hz und Hz/2 | Hinweis; Tipp „FPS-Limit knapp unter der Bildwiederholrate“ (T2) |
| K11 | Speicherleck im Spiel | Privater Speicher des Spielprozesses alle 5 s; Anstieg über mindestens 20 min ohne Plateau | Hinweis im Overlay; Spalte `Spiel_RAM_GB`; Kurve in der Auswertung, Hauptaussage zusammen mit K6 |
| K12 | Bufferbloat (Ping steigt unter eigener Netzlast) | Ping Internet bei wenig Datenverkehr (Grundwert) gegenüber Ping bei Download/Upload | Hinweis; Auswertung vergleicht den Ping mit und ohne Last |

Entwurf der Meldungstexte (Feinschliff bei der Umsetzung):

- K1: „Monitor läuft mit 60 Hz, möglich sind 144 Hz – in den Windows-Anzeigeeinstellungen umstellen“
- K2: „Energiesparmodus aktiv – drosselt Prozessor und Grafikkarte“ / „Notebook im Akkubetrieb – Leistung begrenzt“
- K4: Ruckler-Ursache „Treiber-Unterbrechung (DPC)“; Hinweis „Treiber belegen die CPU (DPC {x} %) – häufig Netzwerk-, Audio- oder RGB-Treiber“
- K5: „Zugesicherter Speicher fast voll ({x} von {y} GB) – Auslagerungsdatei von Windows verwalten lassen oder vergrößern“
- K7: „Resizable BAR aus – im BIOS „Above 4G Decoding“ und „Re-Size BAR“ aktivieren (wirkt nur in manchen Spielen)“
- K8: „Spiel läuft auf {interne GPU} statt auf {dedizierte GPU} – in Windows unter Grafikeinstellungen „Hohe Leistung“ zuweisen“
- K10a: „FPS über der Bildwiederholrate ({hz} Hz) – mit G-Sync/FreeSync ein FPS-Limit knapp darunter setzen (z. B. {hz-3})“
- K10b: „VSync halbiert die Bildrate zeitweise ({hz} ↔ {hz/2} FPS) – G-Sync/FreeSync nutzen oder Grafik leicht senken“
- K11: „Speicherverbrauch des Spiels steigt seit {n} min ({a} → {b} GB) – mögliches Speicherleck, ein Neustart des Spiels hilft vorübergehend“
- K12: „Ping steigt bei Datenverkehr um {x} ms (Bufferbloat) – Downloads begrenzen oder im Router QoS/SQM aktivieren“

## T: Ergänzungen der Tipps

| Nr. | Wo | Ergänzung | Bedingung |
|---|---|---|---|
| T1 | Overlay-Tipp und Auswertung beim Urteil VSYNC/Limit | „Monitor kann {max} Hz – in Windows umstellen, dann sind mehr FPS möglich“ | K1 erkannt |
| T2 | Overlay-Tipp und Auswertung beim Urteil Limit | „Mit G-Sync/FreeSync: FPS-Limit knapp unter {hz} Hz setzen“ | K10 erkannt |
| T3 | Auswertung beim CPU-Limit, Overlay-Tipp nur beim ersten Punkt | (1) Energieplan „Höchstleistung“ testen, wenn „Ausbalanciert“/„Energiesparmodus“ aktiv ist. (2) Intel-Hybrid-CPU unter Windows 10: „Windows 11 verteilt Spiele besser auf P- und E-Kerne“. (3) Speicher-Integrität: „bringt in CPU-lastigen Spielen einige Prozent, schwächt aber den Schutz vor Schadsoftware“ – ausdrücklich als Abwägung, keine Empfehlung | K2/K3, Hybrid-CPU, Windows 10 |
| T4 | Auswertung, Hilfe „Bild und Ruckler“ | Ruckler-Muster lesen: nur beim ersten Spielen → Shader-Kompilierung (wird weniger, Shader-Cache nicht löschen); beim Bewegen durch die Welt → Nachladen; mit der Spielzeit zunehmend → Grafikspeicher, Hitze oder Speicherleck | immer |
| T5 | Auswertung, Ruckler „unklar“ | „Overlays und Aufnahme-Software (Discord, Steam, GeForce Experience, AMD) testweise abschalten“ | häufige Ruckler mit Ursache „unklar“ |
| T6 | Auswertung beim GPU-Limit | „Resizable BAR im BIOS aktivieren“ | K7 erkannt |
| T7 | Overlay-Tipp | Akkubetrieb oder Energiesparmodus geht allen anderen Tipps vor | K2 erkannt |

## Vorgemerkt für später (nicht Teil der Blöcke A–E)

| Nr. | Grund | Mögliche Messung |
|---|---|---|
| V1 | Spiel liegt auf einer Festplatte statt einer SSD | Laufwerk des Spielordners, WMI `MSFT_PhysicalDisk` → MediaType |
| V2 | SSD fast voll | Freier Platz auf dem Laufwerk des Spiels |
| V3 | Overlays, Aufnahme- und RGB-Tools (Discord, Steam, GeForce Experience, AMD, iCUE, Armoury Crate …) | Laufende Prozesse nach Namensliste, ähnlich wie `GameFilter`; als Info, da sie nicht immer stören |
| V4 | AMD X3D: Spiel läuft auf dem Chiplet ohne 3D-Cache | Kerne des Hauptthreads gegenüber der Cache-Topologie (ähnlich wie P-/E-Kerne bei Intel) |
| V5 | Maus mit sehr hoher Abfragerate (4–8 kHz) | Kaum direkt messbar; höchstens als allgemeiner Hinweis bei CPU-Limit

## Reihenfolge

1. **Block A – einmalige Systemprüfung (K1, K2, K3, K7, K9):** neue Klasse für Systemangaben, gelesen beim
   Start und alle 30 s (Monitor und Energieplan können sich ändern). Ausgabe im Selbsttest, im Sitzungskopf
   (`# Monitor:`, `# Energieplan:`, `# Speicher-Integrität:`, `# HAGS:`, `# Resizable BAR:`) und im Kopf
   der Auswertung. Meldungen für K1, K2 und K7.
2. **Block B – laufende Messungen (K4, K5, K10, K12):** DPC-Zähler in PDH, Ruckler-Zuordnung erweitern,
   Commit-Meldung, Sync-Muster, Ping-Grundwert gegenüber Ping unter Last.
3. **Block C – Spielprozess (K11, K8):** privater Speicher des Spiels, GPU-Adapter des Spiels.
4. **Block D – Auswertung (K6, K11, K12):** Verlauf über die Sitzung, Spalten, Kurven, Aussagen.
5. **Block E – Tipps (T1–T7)** und README/LIESMICH.

Nach jedem Block: Tests grün, Vorschau-Bilder in allen drei Ansichten, Rückfrage zum Commit.

## Abnahmekriterien

- Jede neue Meldungskennung ist in `NoteCategories` eingeordnet (der vorhandene Test prüft das).
- Je Kandidat mindestens ein Test mit künstlichen Werten, der Meldung **und** Ausbleiben der Meldung prüft.
- K1, K2, K3, K7, K9: Der Selbsttest auf Marcos PC zeigt plausible Werte (erwartet: 1 Monitor mit seiner
  Höchstrate, Energieplan wie eingestellt, ReBAR an oder aus wie im BIOS).
- K4: Die Ruckler-Zuordnung nennt „Treiber-Unterbrechung“ nur bei erhöhter DPC-Zeit im selben Moment.
- K11/K6/K12: künstliche Sitzungen in der Auswertung (mit und ohne Effekt) zeigen die Aussage bzw. bleiben
  unauffällig; ältere Sitzungen ohne die neuen Spalten erzeugen keine Fehler, nur „Nicht aufgezeichnet“.
- Keine neue Meldung bei normalem Spielen auf Marcos PC (Probelauf mit einem Spiel).
- Die Messungen kosten zusammen weniger als 2 ms je Takt (im Selbsttest gemessen).
- Overlay-Breite bleibt 400 px, die Größe ändert sich nicht von selbst.

## Zu beachten (Prüfung auf Widersprüche)

- K1 und K10 hängen zusammen: K10 braucht die Hz aus K1, also K1 zuerst.
- K2: Unter Windows 11 sagt „Ausbalanciert“ allein wenig, der Energiemodus-Regler legt sich darüber. Deshalb
  den effektiven Modus lesen. Marcos PC läuft mit Windows 10, dort gibt es den Regler nur bei Notebooks.
- K3: Speicher-Integrität ist eine Sicherheitsfunktion. PerfOverlay empfiehlt nicht, sie abzuschalten, sondern
  nennt nur Nutzen und Nachteil (passt zum Grundsatz „ändert nie selbst“).
- K5: Bei einer von Windows verwalteten Auslagerungsdatei wächst das Limit mit, kurze Spitzen über 90 % sind
  dann harmlos. Deshalb muss der Wert einige Sekunden anhalten (wie bei den Netzwerk-Meldungen).
- K6: Unterschiedliche Spielszenen verfälschen den Vergleich. Deshalb nur als vorsichtiger Nebenbefund, außer
  K11 oder die Temperaturen erklären den Abfall.
- K7: Ein Hinweis, kein Engpass. Viele Spiele nutzen ReBAR nicht, ein „Engpass“ wäre irreführend.
- K8: Notebooks mit MUX-Schalter oder Advanced Optimus zeigen das Bild über die interne GPU, rendern aber auf
  der dedizierten. Maßgeblich ist die 3D-Last je Adapter, nicht der Anschluss des Monitors.
- K4: Die Ruckler-Zuordnung prüft zuerst VRAM und Laufwerk. DPC kommt vor „CPU-Spitze“, sonst würde eine
  hohe DPC-Zeit als CPU-Spitze gezählt.
- Frame Generation (DLSS 3/FSR 3) braucht bei NVIDIA HAGS. K9 bleibt deshalb reine Info und rät nicht zum
  Abschalten.
