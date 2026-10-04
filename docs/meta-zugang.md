# Meta-Zugang für „Beitrag aus Link“

Mit diesem Zugang übernimmt ein Beitrag aus Instagram oder Facebook den **vollen Text** und **alle
Bilder eines Karussells in Originalgröße**. Ohne Zugang gibt Meta meist nur ein Bild und einen gekürzten
Text heraus. Der Zugang gilt nur für **das eigene Konto bzw. die eigene Seite** — Meta bietet keinen
Abruf fremder Beiträge an.

Eingetragen wird er pro Website unter **Einstellungen → Social Media**, danach mit „Instagram prüfen“ /
„Facebook prüfen“ kontrollieren. Die App darf im **Entwicklungsmodus** bleiben; eine Prüfung durch Meta
(App Review) ist nicht nötig, solange nur eigene Konten verbunden werden.

> Meta baut seine Oberflächen oft um. Die Schritte stimmen sinngemäß; Knöpfe können anders heißen.

## Instagram

1. **Konto umstellen:** In der Instagram-App → Einstellungen → Kontotyp und Tools → *Zu professionellem
   Konto wechseln* (Creator oder Business). Ein privates Konto geht nicht.
2. **App anlegen:** <https://developers.facebook.com/apps> → *App erstellen* → Anwendungsfall
   *Nachrichten und Inhalte auf Instagram verwalten* (Instagram API) → Typ *Business*.
3. **Konto mit der App verbinden:** Im App-Dashboard → *Instagram* → *API-Einrichtung mit
   Instagram-Login* → Schritt *Zugriffsschlüssel generieren* → *Konto hinzufügen* und mit dem
   Instagram-Konto anmelden. Falls verlangt: unter *App-Rollen → Rollen* das Konto als
   *Instagram-Tester* eintragen und die Einladung in Instagram annehmen (Einstellungen → Apps und
   Websites → Tester-Einladungen).
4. **Schlüssel erzeugen:** Beim verbundenen Konto auf *Token generieren*. Berechtigung
   `instagram_business_basic` genügt. Der Schlüssel beginnt meist mit `IGAA…` und ist 60 Tage gültig.
5. **In MatCMS eintragen:** Einstellungen → Social Media → *Instagram-Zugangsschlüssel* → Speichern →
   *Instagram prüfen* (meldet „Verbunden mit Instagram @…“).

**Verlängerung:** MatCMS verlängert den Schlüssel bei einer Übernahme selbst (höchstens einmal pro
Woche). Wird 60 Tage lang nichts übernommen, läuft er ab — dann Schritt 4 und 5 wiederholen. Die
Prüfung meldet das als „Zugang abgelaufen oder ungültig“.

## Facebook-Seite

1. **App:** dieselbe oder eine neue Meta-App vom Typ *Business* mit dem Anwendungsfall *Alles auf deiner
   Seite verwalten* (Pages API).
2. **Graph API Explorer:** <https://developers.facebook.com/tools/explorer> → App auswählen →
   Berechtigungen `pages_show_list` und `pages_read_engagement` hinzufügen → *Generate Access Token* und
   mit dem Facebook-Konto bestätigen, das die Seite verwaltet.
3. **Dauerhaft machen:** Den erzeugten (kurzlebigen) Nutzer-Schlüssel im *Access Token Debugger*
   (<https://developers.facebook.com/tools/debug/accesstoken>) mit *Zugriffsschlüssel verlängern*
   verlängern. Mit diesem langlebigen Schlüssel im Explorer `me/accounts` abfragen: Beim Eintrag der
   Seite steht ihr **Seiten-Schlüssel** (`access_token`, beginnt mit `EAA…`). Ein so erzeugter
   Seiten-Schlüssel läuft nicht ab.
4. **In MatCMS eintragen:** Einstellungen → Social Media → *Facebook-Seiten-Zugangsschlüssel* →
   Speichern → *Facebook prüfen* (meldet „Verbunden mit der Facebook-Seite …“).

## Gut zu wissen

- Gefunden werden die **letzten rund 400 Beiträge** des Kontos bzw. der Seite.
- **Videos** werden nicht übernommen, nur ihr Vorschaubild.
- Die Schlüssel liegen in den Einstellungen der Website und reisen damit in deren Backups mit — wie das
  SMTP-Passwort. Ein Backup also nicht weitergeben, wenn der Schlüssel darin nicht landen soll, oder ihn
  vorher unter Social Media entfernen.
- Wer den Zugang entziehen will: in Instagram unter *Apps und Websites* bzw. in Facebook unter
  *Business-Integrationen* die App entfernen.
