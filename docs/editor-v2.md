# Seiten-Editor v2

Ziel (Matthias, 2026-10-05; Mockup: `docs/mockups/editor-v2.html`):

1. **Vollbild im MatCMS-Look:** oben links „Zurück“ statt Logo, links die Block-Navigation (wo sonst
   das Admin-Menü steht), Mitte die Vorschau, rechts das gewählte Element zum Bearbeiten.
2. **Mehrere Ebenen:** ein Abschnitt besteht aus groben Elementen (Hero → Bild, Überschrift, Text,
   Buttons → Button). **Spalten**, in denen links und rechts eigene Elemente stehen.
3. **Vorschau ohne Admin-Leiste.**

## Grundlage, die schon da ist

- Das Datenmodell ist ein Baum: `ContentBlock.ParentId`, Container mit `AllowedChildren`, rekursiv
  gerendert (`_BlockList` → `ContainerBlockModel`). **Das Speicherformat bleibt**, bestehende Seiten und
  Backups laufen unverändert.
- Der heutige Editor hält die Seite schon als **Entwurf im Browser**: `Edit?handler=RenderPreview`
  rendert einen Entwurf ohne zu speichern, `Edit?handler=SaveAll` gleicht den ganzen Baum in einer
  Transaktion ab (neue Blöcke mit negativer Id).
- `MatBlockFields.build(container, schema, data)` (admin-blocks.js) rendert die Felder eines Blocks.

## Etappen

**1 — Oberfläche (dieser Schritt).** Neue Seite `/Admin/Pages/Editor/{id}`, layoutlos und im
Vollbild, auf den bestehenden Blöcken: Baum beliebiger Tiefe (auf-/zuklappen, auswählen, hinzufügen
je Ebene nach `AllowedChildren`, duplizieren, löschen, verschieben), Inspector mit den Feldern des
Blocks (gleicher Renderer), Vorschau mit Klick-zum-Auswählen und Geräte-Breiten, Rückgängig/Wiederholen,
ein Speichern für alles (`SaveAll`), Warnung vor ungespeichertem Verlassen. Seiteneinstellungen im
Dialog. Die Vorschau zeigt keine Admin-Leiste mehr. Der bisherige Editor bleibt als „Klassischer
Editor“ erreichbar, bis v2 alles kann.

**2 — Elemente und Spalten.** Neue Bausteine: Abschnitt (Hintergrund, Abstand, Breite), Spalten
(50/50, 33/67, 67/33, drei; vertikal ausrichten; Handy untereinander/umgedreht) mit Spalte, und die
Elemente Überschrift, Text, Bild, Button, Button-Gruppe, Abstand. Vorlagen wie „Hero“ legen einen
Abschnitt mit Elementen an. In der Vorschau lassen sich auch Kinder anklicken (Markierung pro Kind).

**3 — Feinschliff und Umzug.** Ziehen über Ebenen hinweg, KI (Seite erzeugen, Block umschreiben),
Übersetzungen/Sprachversionen und Seitenwechsel in v2; danach wird v2 der Standard und der klassische
Editor entfällt.
