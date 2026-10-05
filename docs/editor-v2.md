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

**1 — Oberfläche (fertig, 2026-10-05).** Neue Seite `/Admin/Pages/Editor/{id}`, layoutlos und im
Vollbild, auf den bestehenden Blöcken: Baum beliebiger Tiefe (auf-/zuklappen, auswählen, hinzufügen
je Ebene nach `AllowedChildren`, duplizieren, löschen, verschieben), Inspector mit den Feldern des
Blocks (gleicher Renderer), Vorschau mit Klick-zum-Auswählen und Geräte-Breiten, Rückgängig/Wiederholen,
ein Speichern für alles (`SaveAll`), Warnung vor ungespeichertem Verlassen. Seiteneinstellungen im
Dialog. Die Vorschau zeigt keine Admin-Leiste mehr. Der bisherige Editor bleibt als „Klassischer
Editor“ erreichbar, bis v2 alles kann.

**2 — Elemente und Spalten (fertig, 2026-10-05).** Neue Bausteine: Abschnitt (Hintergrund, Abstand, Breite), Spalten
(50/50, 33/67, 67/33, drei; vertikal ausrichten; Handy untereinander/umgedreht) mit Spalte, und die
Elemente Überschrift, Text, Bild, Button, Button-Gruppe, Abstand. Vorlagen wie „Hero“ legen einen
Abschnitt mit Elementen an. In der Vorschau lassen sich auch Kinder anklicken (Markierung pro Kind).

  Umsetzung: `Content/ElementBlocks.cs` (Typen `el-section`, `el-columns`, `el-column`, `el-heading`,
  `el-text`, `el-image`, `el-buttons`, `el-button`, `el-spacer`; Elemente sind child-only), Partials
  `Blocks/_El*.cshtml`, Stile in `MatCMS.Shared.Web/wwwroot/css/site.css` (nur Theme-Variablen). Jedes
  Kind eines Containers rendert über `Pages/Shared/_ChildBlock.cshtml`: im Editor eine
  `display:contents`-Hülle mit `data-block-id` (anklickbar, ohne das Layout zu ändern) und ein
  gestrichelter Platzhalter, wenn ein Element noch nichts zeigt. Vorlagen (Hero, Bild + Text,
  Textabschnitt, drei Spalten) baut `editor-v2.js` als Baum aus Elementen.

**3 — Feinschliff und Umzug (größtenteils fertig, 2026-10-05).** Ziehen über Ebenen hinweg, KI (Seite erzeugen, Block umschreiben),
Übersetzungen/Sprachversionen und Seitenwechsel in v2; danach wird v2 der Standard und der klassische
Editor entfällt.

  Fertig: Ziehen über Ebenen (vor/nach jedem Block oder in einen Container, nur wo der neue Elternteil
  den Typ annimmt, nie in sich selbst), KI „Block verbessern“ und „Seite erzeugen“ auf dem Entwurf
  (rückgängig machbar, gespeichert erst mit Speichern), Sprachversionen (wechseln, anlegen,
  maschinell übersetzen, Vergleich) und Seitenwechsel im Titel — die Aktionen des klassischen Editors
  führen mit `back=v2` zurück in v2. Offen: Text direkt in der Vorschau tippen, klassischen Editor
  entfernen (bewusst noch nicht — erst nach Freigabe).
