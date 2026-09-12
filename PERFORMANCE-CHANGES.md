# Performanceänderungen vom 9. September 2026

Die Änderungen betreffen Quellcode und lokale Release-Binaries. Keine Spiel-, Plugin- oder Linux-Systemeinstellungen wurden zur Beschleunigung geändert. Der Nutzer hat die lokal installierten Fixes am 12. September als gut funktionierend bestätigt. Quantitative Spiel-FPS-Messungen liegen nicht vor.

## Radar und gemeinsamer GameHelper-Kern

- Sichtprüfungen für die Pfadglättung und die direkte Wegverbindung brechen am ersten Hindernis ab. Die zählende Linien-API bleibt erhalten.
- Rastergrenzen werden vor der Indexberechnung geprüft, auch bei Tür-Overrides. Negative x-Werte und Zeilenüberläufe erzeugen keine falschen begehbaren Zellen mehr.
- A* verwirft veraltete Queue-Einträge und unterstützt Abbruch während Suche und Glättung.
- Drei Pfad-Caches teilen sich einen Worker-Zugang. Pro Batch höchstens vier Ziele; nach etwa 40 ms wird zwischen abgeschlossenen Suchen abgegeben. Eine einzelne Suche behält ihr bisheriges Suchbudget von einer Million Expansionen. Es handelt sich bewusst um ein weiches Zeitbudget, keine garantierte maximale Framezeit.
- Ältere Suchaufträge haben Vorrang, damit entfernte Ziele nicht verhungern. Erfolglose Suchen warten normalerweise drei Sekunden. Geänderte Ziele/Türzustände oder eine Spielerbewegung von mindestens 50 Rastereinheiten umgehen diese Pause.
- Bei unveränderter Spielerposition werden gültige Pfade bis zur fälligen Vollprüfung wiederverwendet. Teilpfade erhalten auch dann einen wiederverwendbaren Rest, wenn die Glättung den Weg auf wenige Ecken reduziert hat.
- Ergebnisse werden ausschließlich vom Zeichen-Thread übernommen. Kartenwechsel und Deaktivierung brechen alte Jobs ab und trennen alte Cache-Snapshots von neuen. Tests decken späte Ergebnisse alter Karten ab.
- Tür-Overrides werden höchstens alle 250 ms gesammelt und nur bei tatsächlicher Mengenänderung ersetzt. Reine gerade POI-Linien starten keine A*-Jobs mehr.
- Tracked-Objekte werden in einer inkrementellen Baumtraversierung gelesen: höchstens 2048 Knoten bzw. ungefähr 10 ms pro Portion, alle 100 ms. Große Bestände können damit mehrere Portionen benötigen. Neue entfernte Symbole können entsprechend verzögert erscheinen.
- Die Traversierung liest zunächst Identität und Pfad. Nur passende Objekte bekommen vollständige Entity-/Komponentenobjekte. Pfade werden über Adresse, Entity-ID und Metadatenadresse wiederverwendet; ein Zyklenschutz begrenzt beschädigte Live-Bäume. Alte Karteninstanzen erhalten keine neuen Scan-Ergebnisse.
- Erinnerte Radar-Instanzen sind auf die 16 zuletzt verwendeten Karten begrenzt. Wiederbetreten älterer verdrängter Instanzen kann ihre Erreicht-Markierungen zurücksetzen.
- Der allgemeine Entity-Reader reserviert seine Worker-Warteschlangen abhängig vom Bestand mit 32–256 statt pauschal 2000 Einträgen. Bei Bedarf können sie weiterhin wachsen.
- UI-Parent-Caches verwenden ihre Snapshot-Arrays bis zur nächsten Strukturänderung wieder; kleine Bestände werden ohne Parallel-Dispatch aktualisiert. Leere Caches starten keine Worker. Child-Cache-Arrays gleicher Größe werden geleert statt neu angelegt; ihre Inhalte werden weiterhin invalidiert.
- Radar-Hintergrundarbeit ist über die vorhandene Profilansicht unter `Radar.ComputePath` und `Radar.SleepingEntitiesBatch` messbar. Der Profiler wurde nicht automatisch aktiviert.

## Weitere Plugins

| Plugin | Prüfung / Änderung |
|---|---|
| RunecraftHelper | Parallele Tour-Berechnungen erhalten denselben Cache ausdrücklich; maximal zwei Worker. Zusammenhangsraster werden synchronisiert und auf acht Türkonfigurationen je Planung begrenzt. Kartenwechsel verwerfen/stoppen alte Planungen. Linienabbruch, Rastergrenzen und A*-Queue verbessert. |
| RunecraftHelper – Scans | Debug-Weglängen werden nur für eingeschaltete Debug-/Grid-Anzeigen berechnet. Monolith-Auswertung arbeitet portionsweise, mit weichem 2-ms-Budget zwischen Objekten. Rezeptmitgliedschaft wird indexiert/gecacht, Preise bleiben aktuell und werden innerhalb eines Zeichenpasses wiederverwendet. Messpunkt: `RunecraftHelper.MonolithScanSlice`. |
| SekhemaHelper | Dieselben Geometrie-/A*-Verbesserungen, bestehender größerer Suchradius für blockierte Endpunkte bleibt erhalten. Alte Hazard-Routen werden nach Kartenwechsel nicht mehr veröffentlicht; Deaktivierung bricht den Auftrag ab. |
| Atlas2 | Kein Inventar-/Foreground-/Graph-Aufwand bei geschlossener Atlaskarte. Wiederverwendung der zwei Koordinaten-Dictionaries. |
| LootValue | Vollständiger rekonstruierter Quellstand der eingesetzten Version mit Ritual-Unterstützung, inkrementellen Panel-Scans und korrigierter Stash-Scroll-Erkennung. Siehe `Plugins/LootValue/RECOVERY.md`. |
| AutoHotKeyTrigger | Dasselbe für Monsterzählungen und Diagnosescans; Bedingungen, Auslöser und Eingaben unverändert. |
| RitualWispAlert | Direkte Entity-Iteration. |
| PlayerBuffBar | Bereits gefilterte Anzeigeeinträge werden direkt verwendet; kein zweiter Listenkopiervorgang pro Buff-Leiste. Wiederverwendung der Textur-Endungsliste. |
| CampaignHelper | Gebietssuchset wird nur beim Wechsel des Guide-Objekts aufgebaut, nicht pro Frame. |
| StashUtility | Bei geschlossenen großen Panels entfallen Scans, sofern Debug-Probe und Merchant-Overlay ausgeschaltet sind. Für das separate Merchant-Overlay bleibt die bisherige Verarbeitung erhalten. |
| HealthBars | Zeichenpfad geprüft; verwendet bereits direkte Entity-Iteration und die native Geometrieoptimierung. Keine zusätzliche Änderung. |
| PreloadAlert | Ereignis-/Cache-basierter Zeichenpfad geprüft; keine belastbar nötige weitere Änderung. |
| NinjaPricer | Zeichenpfad stößt bereits gedrosselte Hintergrundaktualisierungen an. Anbieter-/Netzwerklogik unverändert; keine Änderung der vorgeschriebenen identischen vendorten Kopie. |
| PickupHelper | Arbeitet am aktuell überfahrenen Item, kein zusätzlicher Vollscan beseitigt. Eingabelogik unverändert. |
| AngeArbitrage | Vorhandenen Quellbaum und Zeichen-/Scan-Einstiege geprüft; bereits begrenzte Beobachtungs-/Scanlogik. Keine spekulative Änderung. |
| WorldDrawing | Zeichenpfad praktisch leer; kein relevanter zusätzlicher Aufwand. |
| DevBridge | Lokaler Quellordner leer, kein passendes Repository im genannten Account gefunden. Keine vollständige Codeprüfung dieses Plugins möglich. |

Das ist eine gezielte Prüfung der häufig aufgerufenen Codepfade, kein vollständiges Profil sämtlicher Spielsituationen. Größere alternative Navigationsarchitekturen (gemeinsame Suchbäume für alle Ziele, hierarchische Navigation, globales Array-Pooling) wurden nicht blind eingeführt: Die konkret belegten Fehler und wiederholten Arbeiten sind behoben, diese Alternativen brauchen reale Kartenprofile und separate Korrektheitsarbeit.

## Verifikation

- Vollständiger Release-Build aller gefundenen lokalen Plugins plus RunecraftHelper und CampaignHelper erfolgreich.
- Linux-Paketbau einschließlich nativem Renderer erfolgreich. Beim ersten Versuch blockierte die Sandbox die Übernahme vorhandener Datei-ACLs; derselbe Paketbau außerhalb dieser Beschränkung gelang.
- 12 RunecraftHelper-Tests, darunter Vergleich der A*-Kosten mit Dijkstra auf 100 deterministischen Zufallskarten und Prüfung des Preis-Caches.
- 65 CampaignHelper-Tests und Import-Layout-Prüfung.
- 8 LootValue-Tests.
- Radar-Probe: 20.000 Linienvergleiche, Grenzen, getrennte Kartenbereiche, Türen, Abbruch, Wiederholpause/Invalidierung, alte Kartenjobs und faire Zielbearbeitung.
- Sleeping-Scanner-Probe: simulierter 5000-Knoten-Baum mit Zyklus, portionsweiser Fortschritt, Filter vor Entity-Konstruktion, Kartenidentität und Abbruch. Das testet die Produktions-Traversierung mit einem simulierten Speicherleser, keinen laufenden PoE2-Prozess.
- Vorhandene Radar-Cadence-, Atlas-Cache-, HealthBars- und Paketprüfungen bestanden; Plugin-Metadatenprüfung für die zehn geänderten Plugins bestanden.
- `git diff --check` in den bearbeiteten Repositories.

Zusätzliche Allokationsmessung: 100 Durchläufe über 10.000 Entities verursachten mit `.Values` 8.008.000 Byte, mit direkter Iteration 6.400 Byte. Das ist ein synthetischer Vergleich dieser Schleifen, keine Messung des gesamten Programms.

Die frühere Glättungsmessung (ca. 6–27× schneller auf gewundenen synthetischen Pfaden) belegt den ursprünglichen Ansatz. Sie ist kein pauschales Versprechen für die jetzt umfassender geänderte Wegsuche oder Spiel-FPS.

## Quellstände und Betrieb

- Hauptquellbaum: `/home/auron/PoE2/GameHelper2-LinuxFork`.
- RunecraftHelper: `/home/auron/PoE2/RunecraftHelper`, basierend auf der lokal verzeichneten Version `4e48f78f60961850df74b23208c8968d3170c5f7`.
- CampaignHelper: `/home/auron/PoE2/CampaignHelper-review`; der Produktionscode des geprüften aktuellen Commits stimmt vor unseren Änderungen mit `c1086e9e2fd38a5af02a09b096ffd393217a5031` überein, lediglich die Projekt-/Testablage wurde upstream umgestellt.
- SekhemaHelper und StashUtility: separate Git-Quellbäume unter `GameHelper2-LinuxFork/Plugins/`; im Hauptrepository bereits durch `.gitignore` ausgeschlossen.
- Sauberes Linux-Paket: `/home/auron/PoE2/GameHelper2-performance-build`.
- Sicherung, Dateimanifest, Rücknahmeprogramm, Quellpatches und Testprotokolle: `/home/auron/PoE2/performance-update-2026-09-09`.

Die laufende Anwendung wird nicht automatisch beendet. Nach der Übernahme werden die neuen Assemblies beim nächsten vollständigen GameHelper-Neustart geladen; für einen Rücktest nach einer Rücknahme ebenfalls neu starten. Plugin-Aktivierungen bleiben wie zuvor. Insbesondere wird RunecraftHelper nicht allein durch dieses Update eingeschaltet.

Veröffentlichung vom 12. September: Core und gebündelte Plugins im GameHelper2-LinuxFork; separate Änderungen in YunaAUbot/RunecraftHelper, YunaAUbot/campaignhelper2, YunaAUbot/SekhemaHelper und YunaAUbot/StashUtility. Die Atlas-Anpassungen sind zusätzlich bereits in YunaAUbot/Atlas enthalten. Updates direkt aus den ursprünglichen fremden Repositories enthalten diese Fork-Anpassungen nicht. Die Sicherungen bleiben als historische Nachweise erhalten.
