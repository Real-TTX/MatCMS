# Moments (Native)

Der Gästebereich einer privaten Veranstaltung – Hochzeit, Geburtstag, Jubiläum, Klassentreffen. Die Website selbst kann öffentlich
bleiben; alles mit Gesichtern und Namen der Gäste liegt hier. Erster Einsatz: grit-steffen.cloud2.matthix.de (Hochzeit 09.10.2026).

Paket: `./pack-native.ps1 -Path ./moments` → `dist/moments-<version>.zip`. Läuft nur mit „Native Plugins erlauben“.

## Was es kann

- **Zugang je Haushalt ohne E-Mail.** Einstellungen → Anmeldung: mit **persönlichem Code** (Standard), mit **Benutzername und
  Passwort** oder mit **beidem** (das Passwort-Formular eingeklappt unter dem Code-Feld).
  - Der Code: standardmäßig acht Zeichen (`K7XM-4QPA`, ohne 0/O/1/I/L); Länge (6–12) und Zeichen (Buchstaben und Ziffern / nur Ziffern /
    nur Buchstaben) sind einstellbar und gelten für neu erzeugte Codes. Je Haushalt kann ein **eigener Code** eingetippt werden
    (`START12345`: mindestens 6 Zeichen A–Z/0–9; Groß-/Kleinschreibung, Leerzeichen und Bindestriche egal). Dazu der persönliche
    Link `/moments/z/{code}`.
  - Benutzername und Passwort vergeben die Gastgeber je Haushalt (Gäste → Haushalt, oder „Mehrere auf einmal“). Das Passwort wird nur
    als PBKDF2-Hash gespeichert – neu setzen ja, ablesen nein.
  - Die **Zugangskarten** (`/moments/karten`, nur für Admins, acht pro A4-Seite) tragen Name, Adresse, Code und den QR-Code des Links
    („Mit der Handykamera scannen“) – im Passwort-Modus den Benutzernamen und eine Zeile fürs Passwort. **Jede Karte einzeln** als Bild
    (PNG; am Handy „Teilen“ → WhatsApp), als PDF (eine Seite in Kartengröße) oder als fertige **WhatsApp-Nachricht** mit Link und Code.
    Alles im Browser gemacht (Canvas, ein paar Zeilen PDF) – der Server hat keine Bild- oder PDF-Bibliothek.
  - „Mehrere auf einmal anlegen“: eine Zeile je Haushalt, `Name | Personen | Tisch | Benutzername | Passwort`.
- **Persönliche Begrüßung** („Willkommen, Zoe & Fabian!“), optional mit Tisch.
- **Galerie** `/moments`: chronologisch mit Stunden-Überschriften, „Neueste“, „Beliebteste“; Filter nach Person, Stichwort, Hochlader,
  „Meine Herzen“; Highlights oben; drei Fotos pro Zeile am Handy.
- **Foto-Seite** `/moments/foto/{id}`: groß, Wischen und Pfeiltasten für vor/zurück, Herz (ohne Neuladen), Herunterladen,
  Personen und Stichworte markieren, Kommentare. Wer hochgeladen hat, kann Bildunterschrift setzen und löschen.
- **Hochladen vom Handy** `/moments/hochladen`: ein großer Knopf, beliebig viele Fotos, jede Kachel füllt sich beim Hochladen.
  Das Handy rechnet Ansicht (2048 px) und Kachel (640 px) selbst aus – das Plugin hat keine Bildbibliothek; das Original geht mit
  (Download), es sei denn, es ist zu groß oder HEIC.
- **Freigabe**: Fotos der Gäste sehen die anderen erst nach Freigabe (Admin → Moments → Freigabe, Zähler im Menü und Hinweis auf dem
  Dashboard). Kommentare optional ebenfalls.
- **Moderation** im Admin: Fotos ausblenden/löschen/als Highlight, Markierungen und Kommentare ausblenden/löschen, Personen &
  Stichworte umbenennen, zusammenführen, löschen. Ein Admin, der die Seite ansieht, hat dieselben Knöpfe auch im Gästebereich.
- **Block „Gästebereich (Moments)“** (`moments_access`) für eine normale Seite: Code-Feld für Besucher, Begrüßung und die neuesten
  Fotos für angemeldete Gäste.
- **Menü**: Angemeldete Gäste bekommen im Hauptmenü einen eigenen Eintrag (Name in den Einstellungen) mit Alle Fotos, Hochladen,
  Meine Fotos – und „Abmelden“ als letzten Punkt daneben (ganz rechts). Ein öffentlicher Menüpunkt zum Gästebereich bekommt die
  Sichtbarkeit „nur nicht angemeldet“.

## Wie es gebaut ist (und warum)

- **Ein Haushalt = ein Konto der Website** (`SiteMember`), angelegt beim ersten Anmelden mit `InviteAsync` an eine Adresse
  `gast-…@moments.invalid` – `.invalid` ist reserviert und kann nie Post bekommen, `InviteAsync` verschickt keine. **Das Plugin
  verschickt überhaupt keine E-Mails.** Weil es ein echtes Konto ist, funktionieren Mitglieder-Seiten, Menü-Sichtbarkeit und
  `ctx.Member` wie überall.
- **Der QR-Code** wird mit der Kamera-App des Handys gelesen, nicht von der Seite – die Anmeldeseite sagt das unter dem Code-Feld,
  sonst sucht man dort einen Scan-Knopf.
- **„Neuer Code“ löscht auch das Konto** (ebenso ein von Hand geänderter Code oder ein neues Passwort): der alte Link gilt nicht mehr UND jedes damit angemeldete Gerät ist draußen (ein neues
  Konto entsteht beim nächsten Anmelden). Wird ein Konto anderswo gelöscht (Admin → Mitglieder), bleibt der Haushalt mit seinem Code
  und bekommt beim nächsten Anmelden ein neues.
- **Code-Raten**: das Formular steht auch in einem Block auf einer normalen Seite und hat dort kein Antiforgery-Token; stattdessen
  zählt `Throttle` Fehlversuche je Adresse (12 in 10 Minuten) – für Codes und Passwörter gemeinsam. Ein unbekannter Benutzername
  kostet dieselbe Rechenzeit wie ein falsches Passwort. Das POST-Limit des Hosts gilt pro Plugin und Adresse über ALLE Routen –
  bei einer Feier lädt die ganze Gesellschaft über die eine Adresse des WLANs hoch, daher 600/min.
- **Standortdaten**: aus dem JPEG-Original wird der GPS-Block der EXIF-Daten geleert (Einträge und Werte genullt, Anzahl 0 – ein
  gültiges leeres Verzeichnis, Ausrichtung und Aufnahmezeit bleiben) und ein XMP-Paket mit GPS-Angaben überschrieben
  (`MomentsImage.StripLocation`). Ansicht und Kachel aus dem Canvas haben ohnehin keine Metadaten.
- **Aufnahmezeit**: EXIF `DateTimeOriginal` des Originals, sonst `lastModified` der Datei vom Handy, sonst jetzt. Sie bestimmt die
  chronologische Reihenfolge; im Admin änderbar.
- **Dateien** liegen privat (`ctx.Files`, `p/{guid}[-v|-t].jpg`), ausgeliefert nur nach Prüfung (`private, max-age=604800`,
  `nosniff`). Ein Foto, das (noch) nicht freigegeben ist, gibt für andere 404 – Seite und Bild.
- **Autor 0** = die Gastgeber (Uploads/Kommentare eines Admins ohne Gästekonto, Name aus „Name der Gastgeber“), **-1** = ein
  entfernter Gast, dessen Fotos geblieben sind.

## Tests

`dotnet test tests/MatCMS.Plugins.Tests --filter MomentsTests` – lädt die gebaute DLL in die ganze Anwendung: Anmelden mit Code und
Link, kein Postfach, „Neuer Code“ wirft alte Geräte raus; Upload wartet auf Freigabe und ist bis dahin für andere 404; GPS ist aus dem
Original entfernt, Datum bleibt; Markieren (Person aus der Gästeliste vs. Stichwort), Herz per Skript, Kommentar bleibt Text;
Gäste in einem Rutsch, Zugangskarten nur für Admins (mit Bild, PDF, WhatsApp); Block und Admin-Seiten, „Abmelden“ ganz rechts im Menü;
kurze Codes nur aus Ziffern, eigene Codes; Benutzername und Passwort (nur der Hash gespeichert; im reinen Passwort-Modus gilt der
Karten-Link nicht); „Meine Herzen“ nur für wen, der eines vergeben hat.
