# Ritual Atlas Line: NinjaPricer-Gewichtung

Automatisch aktiv: `UseNinjaRitualWeights = true`. Unter Atlas → Ritual Atlas Line abschaltbar.

- Gewichte entsprechen dem Exalted-Gegenwert aus dem gemeinsamen Preisprovider.
- Bekannte Mengen werden multipliziert; Omens und benannte Uniques zählen als ein Item.
- Plurale ohne Mengenangabe erhalten den Stückpreis und sind in der Tabelle mit `*` markiert. Die Pooldaten enthalten hier keine belastbare Anzahl; die Rangfolge ist deshalb keine Erlösprognose.
- Ohne verfügbaren Preis gilt das gespeicherte manuelle Gewicht, standardmäßig 0. Vorhandene manuelle Werte bleiben erhalten. Bei aktiver Preisbewertung entsprechen manuelle Werte ebenfalls Exalted-Gewichten.
- Ein höherer Summenwert steht weiter oben. Beide Modifikatoren eines Knotens zählen. Die Wahrscheinlichkeitsgewichte der Vorhersage werden nicht verändert.
- Preise werden maximal alle fünf Sekunden im geöffneten Planer oder in seinen Einstellungen abgefragt, einmal je unterschiedlichem Itemnamen. Keine eigenen Netzwerkanfragen. Nur veränderte Gewichte lösen eine Neusortierung aus.
- Alle 38 unterschiedlichen zuordenbaren Itemnamen wurden im lokalen NinjaPricer-Cache gefunden. Unbekannte Uniques und Tribute-/Reroll-Boni erhalten keinen erfundenen Preis.

Validierung: Gesamtbuild ohne Warnungen/Fehler; `dotnet run --project tests/AtlasRitualPricingProbe -c Release` prüft Mengen, Dezimalwerte, Cache, fehlende/ungültige Preise, Provider-Ausfall und manuelle Ersatzgewichte. Darstellung und echte Route im Spiel noch nicht geprüft.

Installation: Atlas2.dll, Atlas2.pdb und zwei Sprachdateien im Standard-Laufzeitordner und Performance-Paket ersetzt. GameHelper zum Laden neu starten. Keine Konfiguration geändert.

Sicherung und Prüfsummen: `/home/auron/PoE2/atlas-ritual-pricing-20260909-160440`. `manifest.json` enthält jede Zieldatei und ihren Originalpfad. Zum Zurücksetzen GameHelper schließen und die gesicherten Originaldateien an die jeweiligen Ziele kopieren; neue Sprachdateien ohne Original können entfernt werden. Quelländerungen bleiben dabei bestehen.
