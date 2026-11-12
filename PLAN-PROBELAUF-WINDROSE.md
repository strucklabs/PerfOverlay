# Plan: Befunde aus dem Probelauf Windrose (Stand 12.11.2026)

Nur Planung, noch nichts umgesetzt.

Grundlage ist die Windrose-Sitzung vom 12.11.2026 (56 min, 3352 Sekunden, PawnIO aktiv, zusammengeführt aus zwei
Dateien). Die Messwerte stimmen, aber sechs Bewertungen sind falsch, schief oder störend. Marco hat W2–W6 direkt
freigegeben. W1 folgt dem statistischen Verfahren, das an den echten Daten erprobt wurde.

## Testdaten

- Aus der Windrose-Sitzung wird eine verkleinerte Testdatei `tests/PerfOverlay.Tests/Daten/windrose.csv` gezogen,
  mit Sekunde, FPS, Ruckler, DPC, Spiel-RAM, Speicherzusage und toten Verbindungen (ca. 50 KB). Sie enthält nur Spiel-
  und Hardwarewerte, keine persönlichen Daten.
- Die künstlichen Lecks entstehen aus denselben Daten plus 20, 30 bzw. 60 MB/min ab Minute 15.

## Blöcke

### W1 – Speicherleck statistisch erkennen (Overlay und Auswertung)
Bisher: Anstieg über 20 min plus „steigt noch in den letzten 5 min“. Das gab bei Windrose einen Fehlalarm, weil das
Laden am Anfang und das Rauschen (±0,5 GB) mitgezählt wurden.

Neu, das gleiche Verfahren im Overlay (C#) und in der Auswertung (JS):
1. Minuten-Mediane des privaten Speichers (aus den 5-s-Werten bzw. den Sekundenzeilen).
2. Sprünge = Minutenänderung > max(300 MB, 6 × Median der Minutenänderungen der letzten 40 min). Lade- und
   Freigabevorgänge sowie Lücken über 2 min trennen Abschnitte.
3. Ruhige Abschnitte ab 5 min, darin eine Theil-Sen-Steigung (Median aller Paarsteigungen), gewichtet nach Dauer.
4. Leck, wenn in den letzten 40 min:
   - mindestens 20 min ruhige Abschnitte vorliegen,
   - die gewichtete Steigung ≥ 20 MB/min beträgt,
   - mindestens 75 % der ruhigen Zeit steigend sind (≥ 10 MB/min)
   - und dasselbe schon 10 min vorher galt (Wachstum hält ≥ 30 min an).
5. Meldung: „Spiel-Speicher wächst seit {n} min um ~{x} MB/min – bei diesem Tempo Speicher in ~{t} voll“. Die
   Restzeit kommt aus der freien Speicherzusage geteilt durch die Rate, nur wenn sie unter 4 h liegt, sonst entfällt
   der Satzteil.
6. Die Auswertung urteilt selbst aus der Spalte `Spiel_RAM_GB` und übernimmt keine Overlay-Meldungen mehr. Sie
   beschreibt das Leck mit denselben Zahlen (Rate, seit wann). „Leistung sinkt + Leck“ (K6) nutzt dieses Urteil.

### W2 – DPC relativ zur üblichen Last der Sitzung
Bisher: feste Schwelle 20 %. Bei Windrose lag die Treiberlast im Spiel meist bei 12–16 % und in 19 % der Sekunden
über 20 %. Ruckler hingen damit nicht zusammen (6,5 % gegenüber 5,5 % der Sekunden), trotzdem wurden ~300 Ruckler-
Sekunden „Treiber-Unterbrechung (DPC)“ zugeordnet.

Neu:
- Grundwert = Median der DPC-Last der letzten 5 min (Overlay) bzw. der Sitzung (Auswertung).
- Ein Ruckler gilt als DPC-bedingt, wenn die DPC-Last im selben Moment ≥ max(25 %, Grundwert + 10 Punkte) ist.
- Hinweis „Treiber blockieren einen Kern“, wenn die DPC-Last 5 s am Stück ≥ max(25 %, Grundwert + 10) ist oder
  dauerhaft ≥ 35 % (auch ein hoher Grundwert ist ein Befund).
- Auswertung: Die Warnung kommt nur, wenn in Sekunden mit erhöhter DPC-Last (Schwelle wie oben) die Ruckler-Quote
  mindestens doppelt so hoch und mindestens 3 Prozentpunkte höher ist als sonst, bei mindestens 60 solchen Sekunden.
  Sonst höchstens eine Info („Treiberlast im Spiel hoch, aber ohne Einfluss auf Ruckler“), und nur bei Grundwert ≥ 20 %.

### W3 – Dauerhinweise nicht dauerhaft anzeigen
Bisher: „Monitor auf 120 statt 144 Hz“ stand 3352 s in der Hinweisliste, „3 tote Verbindungen“ 3098 s. Damit waren
zwei von drei Hinweiszeilen dauerhaft belegt.

Neu (nur Anzeige im Overlay, die Meldungen bleiben in der Aufzeichnung vollständig):
- Gleichbleibende Hinweise erscheinen beim ersten Auftreten 30 s lang, bei jeder Änderung des Textmusters (z. B. andere
  Anzahl) erneut, sonst alle 10 min für 30 s.
- Gilt für: monitor-hz, rebar, power-saving, power-battery, net-closewait, commit, game-memleak, timer
  (Engpass-Kategorie bleibt, aber gleiche Wiederholung), pcie-gen/-width, ram-xmp/-channel (die beiden haben schon eine
  eigene Startlogik und werden darauf umgestellt).
- Engpass-Meldungen zum aktuellen Geschehen (Ruckler, Drosselung, VRAM …) bleiben unverändert.

### W5 – Ladebild und Menü aus den Durchschnitten herausnehmen
Bisher: 12 Sekunden mit 660–1093 FPS (Ladebild/Menü) hoben Ø FPS auf 72, der Median lag bei 67.

Neu (Auswertung): Eine Sekunde gilt als „Ladebild/Menü“, wenn FPS > 3 × Median der Sitzung oder FPS < 0,25 × Median
bei gleichzeitig starkem Nachladen (> 2000 Seiten/s). Diese Sekunden zählen nicht zu Ø FPS, 1%-Low, Bildzeit,
Ruckler/min und den Engpass-Anteilen. Ein Satz in „Bild und Ruckler“ nennt, wie viele Sekunden ausgeklammert wurden.

### W4 – Vergleich „mit/ohne tote Verbindungen“ fair machen
Bisher: Die Phase ohne tote Verbindungen waren fast nur die ersten Minuten mit dem Laden (21 Ruckler/min), verglichen
wurde also Laden mit Spielen. Der Text „zeitweise bis zu 3“ untertrieb, es waren 93 % der Zeit.

Neu:
- Ladebild-Sekunden (W5) und die erste Minute nach Sitzungsbeginn zählen nicht zum Vergleich.
- Der Vergleich braucht je Phase mindestens 5 min, sonst: „kein Vergleich möglich – fast die ganze Sitzung mit toten
  Verbindungen“.
- Die Formulierung richtet sich nach dem Anteil: ≥ 90 % „fast durchgehend“, ≥ 30 % „über weite Strecken“, sonst
  „zeitweise“.

### W6 – Sekundenzählung ohne doppelte Werte
Bisher: Die Spalte „Sekunde“ ist die gerundete Zeit seit Sitzungsbeginn. Bei Abständen um 0,85–1,15 s entstehen
gleiche Werte (8× in dieser Sitzung).

Neu: Sekunde = max(vorige + 1, gerundete Zeit). Lücken bleiben als Sprung erhalten, Werte sind streng steigend.

## Reihenfolge

W1 → W2 → W3 → W5 → W4 → W6. W4 nutzt die Ladebild-Erkennung aus W5. Nach jedem Block: Tests grün, Rückfrage zum
Commit. Am Ende ein Testpaket 0.5.2.

## Abnahmekriterien

1. **W1:** Mit den Windrose-Daten gibt es weder im Overlay (Wiedergabe der 5-s-Werte) noch in der Auswertung einen
   Leck-Alarm. Die künstlichen Lecks mit 20, 30 und 60 MB/min werden spätestens 30 min nach Beginn erkannt (Overlay und
   Auswertung). Die Meldung nennt eine Rate, die ±25 % zur eingebauten Rate passt.
2. **W2:** Mit den Windrose-Daten sinken die DPC-Zuordnungen von ~300 auf unter 30 Ruckler-Sekunden, und die
   Auswertung gibt keine Warnung zur Treiberlast. Künstlich nachgestellt: DPC-Spitzen mit gehäuften Rucklern erzeugen
   die Warnung weiterhin.
3. **W3:** In einer Wiedergabe von 56 min ist der Monitor-Hinweis höchstens 7 × 30 s sichtbar, in der Aufzeichnung
   steht er weiter jede Sekunde.
4. **W5:** Windrose zeigt Ø FPS ≈ Median (Abweichung unter 3 FPS) und den Satz über die ausgeklammerten Sekunden.
5. **W4:** Windrose meldet „fast durchgehend … kein Vergleich möglich“. Die Testsitzung `tot-bremst` aus der CLOSE_WAIT-
   Prüfung zeigt den Vergleich weiterhin.
6. **W6:** Keine doppelten Sekunden mehr (Test mit leicht schwankendem Takt).
7. Alle bisherigen Tests grün. Alte Sitzungen öffnen sich ohne Fehler.

## Zu beachten (Prüfung auf Widersprüche)

- **Gleiches Verfahren an zwei Stellen** (C# und JS) bei W1/W2: Beide Umsetzungen werden mit denselben Testdaten geprüft,
  damit Overlay und Auswertung nicht widersprechen.
- **W3 und Aufzeichnung:** Die Unterdrückung betrifft nur die Anzeige. Die Auswertung (Meldungsdauer, Akkubetrieb-Anteil
  für T7) bleibt korrekt.
- **W5 und Engpass-Anteile:** Ladebild-Sekunden aus den Anteilen herauszunehmen verschiebt die Anteile leicht. Das ist
  gewollt, denn Ladebilder sind kein Engpass.
- **W1-Grenze:** Lecks unter ~20 MB/min gehen bei großen natürlichen Schwankungen statistisch unter. Das wird in der
  README genannt.
- **W2:** Der Grundwert braucht Daten. In den ersten 5 min gilt die feste Schwelle 25 %.
