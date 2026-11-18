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

Mit leerer Hand öffnet der Klick das Menü „Hexe ausrüsten“ bzw. „Narr ausrüsten“; der gelbe Knopf über ihr (links neben dem ×) tut das immer; beide Knöpfe erscheinen, wenn die Maus über der Figur ist (auf dem Handy sind sie immer da). Das × schickt sie weg
(nur ihr Kopf schaut dann über den Rand und holt sie zurück). Die Wahl merkt sich der Browser (`localStorage`), der Server speichert nichts.

## Aufbau

- `plugin.csx` – Einstellungen (Figur, Ecke, nur Startseite, Handy), die Route `/narrenfigur/config` und eine Zeile Body-HTML, die
  die beiden Dateien lädt. Das Body-HTML entsteht EINMAL beim Laden des Plugins; deshalb fragt das Skript die Einstellungen über
  die Route ab, statt sie im HTML zu finden.
- `assets/figur.js` – zeichnet die Figur (ein SVG, keine Bilddateien) und spielt sie. `assets/figur.css` – ihr Aussehen; das Menü
  folgt den Farben der Website (`--accent`, `--font-head`).
- Deutsch und Englisch: Einstellungen über die Tabelle in `plugin.csx`, die Worte der Figur in `figur.js` (nach `<html lang>`).
- Wer „Bewegung reduzieren“ eingestellt hat, bekommt keinen Flug und nur wenig Konfetti.

## Die Figuren

- **Wurmberghexe** (seit 1.0) und **Schalksteinnarr** (seit 1.2: lachende Holzmaske mit roter Nase, struppiges Haar, Haube und
  Zackenkragen halb rot, halb blau mit Schellen, cremefarbenes Häs mit Weinberg, Trauben und Weinglas, rot-blaue Zacken am Saum und
  an den Hosenbeinen, weißes Fell über der Schulter).
- Die Einstellung **Figur** ist die, die ein Besucher zuerst sieht. Mit **„Besucher können die Figur wechseln“** (an) steht unten im
  Menü „Figur“ mit beiden Köpfen; die Wahl merkt sich der Browser (`narrenfigur.figure`), was sie in der Hand hat, bleibt.
- Beide Zeichnungen haben denselben Rahmen (Hand bei 151/178, Winkarm ab 71/121, Kopf im Ausschnitt `60 14 80 110`), deshalb passen
  Gegenstände, Flug und Bewegungen zu jeder. Eine weitere Figur = eine Zeichnung in `FIGURES`, ihre Worte in `T` (beide Sprachen) und
  eine Option der Einstellung in `plugin.csx`.

Tests: `NarrenfigurTests` (jeder Text in beiden Sprachen; die Seite trägt die Dateien, die Route antwortet).
