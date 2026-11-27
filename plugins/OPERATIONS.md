# Plugin-Operationen

**Regel:** Jedes Plugin mit eigenem Inhalt beschreibt, was man damit tun kann, als **Plugin-Operationen**
(`IPluginBuilder.Operation(new PluginOperationSpec(…), handler)`, im Skript `Plugin.Operation(…)`): auflisten, lesen,
anlegen, ändern und löschen bzw. ausschalten — jeweils mit einer deutschen Beschreibung samt Argumenten und einem
Beispiel (`Arguments`). Nur so können KI-Agenten (der KI-Assistent der Website, der MCP der Cloud, der eigene
MCP-Server der Website) mit seinen Daten arbeiten. Lesende Operationen tragen `Writes: false`; was persönliche Daten
von Besuchern zeigt (eine E-Mail-Adresse, eine noch nicht freigegebene Einsendung mit dem Namen des Absenders), trägt
`PersonalData: true` und wird dem KI-Assistenten nie angeboten. Jede schreibende Operation antwortet mit einem `undo`,
das eine **interne** Operation desselben Plugins nennt (`Internal: true` — nur der Host führt sie aus, beim
„Rückgängig“). Eingaben werden geprüft wie im Admin-Formular des Plugins; Texte, die als HTML gezeigt werden, laufen
durch die Bereinigung, die das Plugin ohnehin benutzt.

Ein Plugin **ohne eigenen Inhalt** (nur Einstellungen oder die Anzeige von etwas, das anderswo liegt) bekommt keine
Schein-Operationen, sondern steht hier mit dem Grund.

Antwortform: `{"status": "applied"|"skipped-exists"|"failed", "message": "…", "data"?: …, "undo"?: {"name": "<Operation>", "args": {…}}}`.
Angesprochen wird eine Operation als `{Plugin-Key}.{Name}` (`leserstimmen.review.list`).

| Plugin | Operationen | Ausnahme-Grund |
| --- | --- | --- |
| `leserstimmen` | `review.list`, `review.read` (nur Veröffentlichtes, nie E-Mail), `review.pending` (wartende mit Absenderdaten — PersonalData), `review.create`, `review.update` (auch Freigeben/Verbergen), `review.delete`; intern `review.remove`, `review.restore` | — |
| `bewertungen` | `review.list`, `review.read`, `review.pending` (PersonalData), `review.update` (nur Freigabe: published), `review.delete`; intern `review.restore` — kein Anlegen/Ändern von Text: die Stimmen kommen nur von Besuchern, der Admin hat keine Eingabe von Hand | — |
| `google-reviews` | `review.list`, `review.read`, `review.create`, `review.update`, `review.delete` (die von Hand eingetragenen Bewertungen des Modus „Manuell“); intern `review.remove`, `review.restore` | Konfiguration (API-Schlüssel) und der Cache der Places-API sind kein Inhalt und bleiben außen vor |
| `todo-verwaltung-beispiel` | `todo.list`, `todo.read`, `todo.create`, `todo.update`, `todo.delete`; intern `todo.remove`, `todo.restore` | — (Beispiel dafür, wie ein Skript-Plugin seine Daten beschreibt) |
| `coverage-map` | keine | Kein eigener Speicher: die Länder/Regionen mit Status und Hinweis stehen im Feld „list“ des Blocks auf der Seite — Agenten ändern sie mit den Block-Werkzeugen (`update_block`), wie jedes andere Block-Feld |
| `cookie-consent` | keine | Nur Einstellungen (Kategorien, Texte, eingefügter Code je Kategorie); die Wahl der Besucher liegt in deren Cookie. Eingefügter Skript-Code gehört nicht in die Hand eines Agenten |
| `store-einblick` | keine | Zeigt nur den öffentlichen Katalog einer MatCMS-Cloud an; kein eigener Inhalt |
| `store-einblick-native` | keine | Wie `store-einblick` (dieselbe Code-Datei, als DLL) |
| `blog` | eigene Operationen (siehe Plugin) | |
| `booking` | eigene Operationen (siehe Plugin) | |
| `bot` | eigene Operationen (siehe Plugin) | |
| `docs` | eigene Operationen (siehe Plugin) | |
| `forum` | eigene Operationen (siehe Plugin) | |
| `matcms-store` | eigene Operationen (siehe Plugin) | |
| `moments` | eigene Operationen (siehe Plugin) | |
| `shop` | eigene Operationen (siehe Plugin) | |
| `verein` | eigene Operationen (siehe Plugin) | |

Tests: `tests/MatCMS.Plugins.Tests/ScriptPluginOpsTests.cs` lädt die Skript-Plugins in der ganzen Anwendung und prüft je
Plugin: jede Operation ist da (mit ihren Kennzeichen), Anlegen → Lesen → Ändern → Rückgängig kommt zum Anfang zurück,
falsche Eingaben werden abgelehnt.
