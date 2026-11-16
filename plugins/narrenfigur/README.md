# Narrenfigur

Eine kleine Fasnetsfigur in einer Ecke der öffentlichen Website – zuerst die **Wurmberghexe** der Schalksteinnarren Besigheim
(Holzmaske, langes Haar, blaues Kopftuch mit grünen Fransen, weiße Schürze mit Flicken über dem weinroten Rock).
Besucher klicken sie an und rüsten sie aus:

| Gegenstand | Ein Klick auf die Figur |
|---|---|
| Konfettibeutel | sie wirft Konfetti |
| Besen | sie fliegt quer über die Seite und kommt zurück |
| Schellen | sie läutet (Ton aus dem Browser, keine Datei) |
| Glühwein | sie prostet |

Mit leerer Hand öffnet der Klick das Menü „Hexe ausrüsten“; der gelbe Knopf an ihrer Schulter tut das immer. Das × schickt sie weg
(nur ihr Kopf schaut dann über den Rand und holt sie zurück). Die Wahl merkt sich der Browser (`localStorage`), der Server speichert nichts.

## Aufbau

- `plugin.csx` – Einstellungen (Figur, Ecke, nur Startseite, Handy), die Route `/narrenfigur/config` und eine Zeile Body-HTML, die
  die beiden Dateien lädt. Das Body-HTML entsteht EINMAL beim Laden des Plugins; deshalb fragt das Skript die Einstellungen über
  die Route ab, statt sie im HTML zu finden.
- `assets/figur.js` – zeichnet die Figur (ein SVG, keine Bilddateien) und spielt sie. `assets/figur.css` – ihr Aussehen; das Menü
  folgt den Farben der Website (`--accent`, `--font-head`).
- Deutsch und Englisch: Einstellungen über die Tabelle in `plugin.csx`, die Worte der Figur in `figur.js` (nach `<html lang>`).
- Wer „Bewegung reduzieren“ eingestellt hat, bekommt keinen Flug und nur wenig Konfetti.

## Eine weitere Figur (Schalksteinnarr)

Vorgesehen, noch nicht gezeichnet (es fehlt ein Foto des Häs): eine zweite Zeichnung in `FIGURES` (`figur.js`), eine weitere Option
der Einstellung `figure` in `plugin.csx`, und die Route gibt den gespeicherten Wert aus.

Tests: `NarrenfigurTests` (jeder Text in beiden Sprachen; die Seite trägt die Dateien, die Route antwortet).
