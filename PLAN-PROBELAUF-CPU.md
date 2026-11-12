# Plan: Befunde aus den Probeläufen CK3 und 7 Days to Die (Stand 12.11.2026)

E1 und E2 umgesetzt am 12.11.2026 (E2 geprüft: 7DtD G-Sync-Tipp, CK3 Satz zur Bildwiederholrate, Testsitzung mit 60 FPS an 120 Hz weiter „Limit anheben“). E3 umgesetzt am 12.11.2026. Offen: Testpaket 0.5.3.

Grundlage sind zwei Sitzungen vom 12.11.2026 mit Version 0.5.2:

- **Crusader Kings III** (26 min): Das Spiel lief fast durchgehend bei 120 FPS, auch nach dem Abschalten von VSync. Die
  CPU war nie der Engpass, der stärkste P-Kern lag bei höchstens 56 %.
- **7 Days to Die** (22 min): Das Menü lief mit 118 FPS, danach war das Spiel GPU-limitiert bei rund 135 FPS über
  120 Hz. Ab Minute 17 war die CPU der Engpass: Hauptthread 97 %, CPU gesamt nur 27 %.

Die Korrekturen aus 0.5.2 haben gehalten: kein Fehlalarm bei Leck oder DPC, die Ladebilder wurden erkannt (51 s bei
7DtD), keine doppelten Sekunden. Der Fehlalarm „GPU drosselt wegen Temperatur“ ist schon behoben (106947c).

Drei Bewertungen sind schief:

## Blöcke

### E1 – FPS-Limit nicht als CPU-Limit werten (Overlay)
Bisher: Bei CK3 stand in 13 % der Zeit „CPU-LIMITIERT“, obwohl das Spiel fest bei 119,9 FPS lag. Der Begrenzer des
Spiels wartet innerhalb des CPU-Frames, und PresentMon zählt diese Wartezeit als CPU-Arbeit (CPU-Busy 8,3 ms bei
8,4 ms Bildzeit). Die bestehende Ausnahme dafür greift nur, wenn der Hauptthread unter 70 % liegt. Bei CK3 schwankte
er um 55–66 % mit Ausreißern darüber.

Neu: Liegt die Bildrate stabil auf einem typischen Limit (bestehende Prüfung `IsSteadyCap`: Mittel ≈ Median, ±2 % an
30/60/…/120/144 FPS usw.), gilt das als „FPS-LIMIT“, solange der Hauptthread unter 90 % liegt. Ein echtes CPU-Limit
trifft selten genau ein typisches Limit, und wenn doch, ist der Hauptthread voll.

### E2 – Tipps zum FPS-Limit passend zur Bildwiederholrate (Auswertung)
Bisher: Bei 7DtD kam der Anteil „FPS-Limit“ (31 %) fast nur aus dem Menü. Die Auswertung riet „Für mehr FPS das Limit
anheben, z. B. auf die Bildwiederholrate“. Im Spiel lag die Bildrate aber über 120 Hz, und eine Meldung empfahl
gleichzeitig, ein Limit zu setzen. Das widerspricht sich.

Neu:
- Die Grenze des Limits ist der Median der FPS in den Limit-Sekunden. Zusammen mit den Monitor-Hz aus dem Sitzungskopf
  ergeben sich drei Fälle:
  - Liegt die Grenze deutlich unter der Bildwiederholrate (mehr als 5 FPS darunter), bleibt der Tipp „Limit anheben“.
  - Liegt sie an der Bildwiederholrate, entfällt der Tipp. Stattdessen: „Die Bildrate lag an der Bildwiederholrate des
    Monitors – mehr FPS sind dann nicht sichtbar.“ Den Hinweis auf die möglichen Monitor-Hz gibt es schon (T1).
  - Lag die Bildrate in mindestens 5 % der Spielzeit über der Bildwiederholrate, galt das Limit nur zeitweise, z. B. im
    Menü. Dann kein „anheben“, sondern der G-Sync/FreeSync-Tipp (Limit knapp unter der Bildwiederholrate).
- Das gilt auch, wenn der Engpass wechselt und die Limit-Tipps nur als Teil erscheinen.

### E3 – Ruckler bei ausgelastetem Hauptthread benennen (Overlay)
Bisher: Die meisten Ruckler hießen „unklar (z. B. Shader-Kompilierung)“, bei 7DtD 527 Sekunden lang. Bei 7DtD lag
der Hauptthread in 52 % der Ruckler-Sekunden bei mindestens 95 %, sonst nur in 29 %. Bei Windrose war es 12 % gegenüber
3 %, bei CK3 10 % gegenüber 7 %. Ein Zusammenhang besteht also, ist aber nicht eindeutig.

Neu:
- Eine neue Ursache „Hauptthread voll (Spielberechnung)“, wenn der Hauptthread im Moment des Rucklers mindestens 95 %
  erreicht. Die Reihenfolge der Prüfung: VRAM, Laufwerk, DPC, Speicherzusage, CPU-Spitze, **Hauptthread**, unklar.
- Der Hauptthread-Wert kommt in den Verlauf für die Ruckler-Zuordnung (wie DPC und Nachladen).
- Der Tipp der Auswertung zu „unklar“ bleibt. Er zählt nur noch die echten „unklar“-Ruckler.

## Reihenfolge

E1 → E2 → E3, nach jedem Block Tests und Rückfrage zum Commit. Am Ende ein Testpaket 0.5.3 (enthält auch die
GPU-Temperaturkorrektur).

## Abnahmekriterien

1. **E1:** Nachgestellt mit stabil 120 FPS, CPU-Busy gleich Bildzeit und Hauptthread 60–85 % ergibt „FPS-LIMIT“. Ein
   echtes CPU-Limit (Hauptthread 97 %, schwankend um 100 FPS) bleibt „CPU-LIMITIERT“. Stabil 120 FPS mit Hauptthread
   ≥ 90 % bleibt ebenfalls CPU-limitiert.
2. **E2:** Die 7DtD-Sitzung zeigt keinen Tipp „Limit anheben“, aber den G-Sync/FreeSync-Tipp. CK3 zeigt kein
   „anheben“, sondern den Satz zur Bildwiederholrate. Eine nachgestellte Sitzung mit Limit 60 FPS an einem 120-Hz-Monitor
   zeigt „Limit anheben“ weiterhin.
3. **E3:** Ein Ruckler bei Hauptthread 97 % ohne andere Ursache heißt „Hauptthread voll (Spielberechnung)“. Mit Nachladen
   im selben Moment bleibt „Nachladen vom Laufwerk“ vorn.
4. Alle bisherigen Tests grün, alte Sitzungen öffnen sich ohne Fehler.

## Zu beachten (Prüfung auf Widersprüche)

- **E1 und alte Sitzungen:** Die Auswertung liest das Urteil aus der Aufzeichnung (Spalte `Engpass`). Alte Sitzungen
  wie CK3 behalten deshalb ihre 13 % „CPU-limitiert“. Erst neue Aufnahmen profitieren.
- **E1 und echte CPU-Limits genau auf 60 FPS** (z. B. Konsolen-Portierungen mit festem 60-FPS-Takt): Dort ist der
  Hauptthread meist nicht voll. Als Limit zu gelten ist dann richtig, denn das Spiel begrenzt.
- **E2 nutzt den Sitzungskopf `# Monitor`.** Ältere Sitzungen ohne diese Zeile behalten die bisherigen Tipps.
- **E3:** Ein voller Hauptthread ist ein Hinweis, kein Beweis. Die Formulierung lautet deshalb „Hauptthread voll“ und
  nicht „verursacht durch“. Auch Shader-Kompilierung belastet den Hauptthread. Das „z. B. Shader-Kompilierung“ bleibt
  deshalb beim Fall „unklar“.
- **Nicht Teil dieses Plans:** Menü-Zeiten am Limit aus den Engpass-Anteilen herausnehmen. Dafür fehlt ein sicheres
  Erkennungsmerkmal (Menüs laufen oft mit normalen FPS). E2 behebt die eigentliche Folge, den falschen Tipp.
