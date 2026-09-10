# Upstream-Übernahme vom 10.09.2026

Geprüfter stabiler Upstream: Gordin/GameHelper2, main,
`47aeacd7a6ce98da708c97d903c8fc488c0d348d`.
Zwei neue Commits gegenüber unserem Fork:

- `db1846896b4142ae5495111ca41e6b258bb29dfd`: automatischer Preisprovider-Fallback.
- `47aeacd7a6ce98da708c97d903c8fc488c0d348d`: kürzere Anfrage-Timeouts.

## Integration

Die Funktion wurde in den gemeinsamen NinjaPricer portiert. Der Upstream-eigene
`LootValue/PoeNinjaPriceFetcher.cs` bleibt entfernt, da alle Preisnutzer im Fork
über `PriceProviderRegistry` auf NinjaPricer zugreifen. LootValue wird nicht ersetzt.
Die Integration wird als lokaler Portierungscommit und anschließender Merge mit
`--strategy=ours` dokumentiert: Der Merge markiert genau diese zwei bereits
adaptierten Upstream-Commits als übernommen und behält den geprüften Fork-Baum.
Die zusätzlichen historischen Changelog-Behauptungen über Radar-Minimap-Parität
werden nicht übernommen: Diese Funktion wurde im Fork zuvor bewusst zurückgenommen.

- Je Anfrage zehn Sekunden Deadline für Header und Body, bei Netzwerkfehler/Timeout
  ein Wiederholungsversuch; danach höchstens ein Wechsel zum anderen Dienst.
- Ungültige oder leere Daten führen ebenfalls zum Ersatzanbieter. Unvollständige
  Daten des fehlgeschlagenen Anbieters werden nicht mit dem Ersatz vermischt.
- Bei Ausfall beider Dienste bleiben vollständige alte Preise und Retry-Backoff erhalten.
- Die gespeicherte Anbieterpräferenz bleibt erhalten. Aktiver Anbieter und aktuelle
  Umschaltphase erscheinen in NinjaPricer; Preisabfragen melden die tatsächliche Quelle.
- Cache merkt sich die tatsächliche Quelle und Präferenz getrennt. Alte Caches bleiben
  lesbar. Ein bewusster Quellen-/Ligawechsel verwirft unpassende Daten wie bisher.
- Scout bleibt nutzbar, wenn die optionale Ninja-Ergänzung ausfällt. Ist Ninja im selben
  Durchlauf bereits ausgefallen, wird diese Ergänzung übersprungen.
- Shutdown und veraltete Generationen können weder Preise noch Cache überschreiben.

Kanonischer NinjaPricer: `/home/auron/PoE2/GameHelper2-NinjaPricer`, Basis `3c26629`.
Die vendorte Kopie ist einschließlich des aktuellen `test/`-Layouts bytegleich.
Der Layout-Commit ändert keine Preislogik und verhindert, dass Testprojekte vom
Git-Plugin-Importer als zusätzliche Plugins behandelt werden.

## Prüfung und Installation

46 Tests im eigenständigen NinjaPricer und erneut im Fork bestanden. Geprüft sind
u.a. beide Fallback-Richtungen, Timeouts, ein kurzzeitiger Fehler, beide Dienste
ausgefallen, Cache-Herkunft und vorhandene Schutztests. Der Atlas-Preisprobe-Test
besteht ebenfalls. Importer-Layout geprüft; Gesamtbuild erfolgreich (drei bereits
bekannte Host-Warnungen), finaler Linux-Paketbuild ohne Warnungen oder Fehler.
Echte Dienstausfälle und die Anzeige im Spiel wurden nicht live getestet.

Installiert sind nur NinjaPricer.dll, NinjaPricer.pdb und zwei Sprachdateien in:

- `/home/auron/PoE2/GameHelper2-LinuxFork/dist/GameHelper2-linux/Plugins/NinjaPricer`
- `/home/auron/PoE2/GameHelper2-performance-build/Plugins/NinjaPricer`

441 andere DLLs und sämtliche Konfigurationen blieben bei der Installation bytegleich.
Insbesondere bleibt die reparierte LootValue-Ritual-DLL erhalten. Die zuvor vorhandenen
lokalen Codeänderungen wurden per Prüfsummen gegengeprüft und nicht mitcommittet.
GameHelper zum Laden vollständig schließen und über den normalen Starter neu starten.

Sicherung, Manifeste und Testprotokolle: `/home/auron/PoE2/upstream-sync-20260910-174022`.
Das dortige `validation-package` dient nur dem Paket-Bautest, nicht als Ersatz für die
individuell installierte Laufzeitumgebung mit zusätzlichen Plugins und LootValue-Fix.

Rollback der installierten NinjaPricer-Dateien (GameHelper vorher schließen):
`python3 /home/auron/PoE2/upstream-sync-20260910-174022/restore.py`.
Mit `--check` werden nur Ziel- und Sicherungsprüfsummen geprüft. Der Rollback ändert
keine Einstellungen, keinen Quellcode und keine Git-Commits. Es wurde nichts gepusht.
