# Buchungen (natives Plugin)

Ferienhäuser, Villen und Apartments für eine MatCMS-Seite, gebaut nur gegen `MatCMS.Plugin.Abstractions`. Key `booking`, Admin unter
Admin → Buchungen. Entwurf und Phasen: [`docs/booking-plugin.md`](../../docs/booking-plugin.md). Erste Seiten: Villa Nika, Villa Sogi.

## Stand: 0.8 — Zeitraum-Picker statt großer Kalender, Knöpfe der Seite

- **0.8: Knöpfe sind die Knöpfe der Seite** (`class="btn bk-btn"`; `btn-outline` für den zweiten): Schrift, Großschreibung und Ecken kommen vom Template, das
  eigene Aussehen `.bk-btn:not(.btn)` gilt nur noch, wo ein Template keine `.btn` hat. Die Unterkunfts-Karten sind eine Spalte (nicht mehr ein Grid, das die
  Zeilen streckte — der Knopf wuchs mit dem Text): Preis und Knopf stehen unten, über alle Karten auf einer Linie.

- **Das Anfrageformular hat EIN Feld „Anreise – Abreise“**: der Zeitraum-Picker der Website (`mat-datepicker.js`, derselbe wie im Formular-Builder),
  der über `GET /buchung/belegung?einheit=&von=&bis=` (höchstens 400 Tage, nur Nächte, nie Gäste) lernt, was belegt ist: belegte Nächte durchgestrichen,
  kein Zeitraum über eine belegte Nacht, die Abreise am Wechseltag möglich, Mindestaufenthalt und Anreisetage aus der Saison der ANREISE-Nacht
  (wie `Pricing.Calculate`), Pufferstage der Einheit eingerechnet. Das Formular postet `zeitraum=a..b`; ohne Script zwei normale Datumsfelder
  (`<noscript>`), die Anfrage nimmt beides. Felder im Aussehen des Kontaktformulars (`.form-field` aus site.css).
- **„Belegungskalender“ kompakt**: zwei Monate nebeneinander (Handy: einer), Pfeile blättern durch bis zu 12.
- Die Picker-Dateien bindet der Block selbst ein (`/css|js/mat-datepicker.*?v=busy1`). Auf einem älteren Host ohne Belegung im Picker wählt der
  Gast trotzdem, der Preis darunter sagt „belegt“.

## Stand davor: Phase B6 (0.6) — alle Phasen des Entwurfs gebaut

### Kundenverbund (B6)

Mehrere Seiten mit denselben Gästen (Villa Nika + Villa Sogi) teilen eine Gästekartei — ohne Cloud. Einstellungen → Kundenverbund, Rolle:

- **für sich** (Standard): eigene Kartei.
- **führt die Kartei**: bietet ihre Gäste über die Plugin-API an — `GET /api/v1/booking/guests?email=…` (ein Gast mit seinen Aufenthalten auf allen Seiten),
  `GET …/guests?since=…&page=` (geänderte), `PUT …/guests` (anlegen/abgleichen, Antwort = der Gast, wie er jetzt dasteht), `POST …/stays` (Aufenthalte einer
  anhängenden Seite als Zusammenfassung). Zugang: ein Site-API-Schlüssel mit dem Recht `booking:personal` (Admin → API-Schlüssel); eine Seite, die nicht führt,
  antwortet 409. Unter Buchungen → Kundenverbund stehen die Seiten, die schon gemeldet haben (nach dem Namen ihres Schlüssels).
- **hängt an**: Adresse + Schlüssel der führenden Seite. Ein Job (alle 10 Minuten, oder „Jetzt abgleichen“) schickt jede seit dem letzten gelungenen Lauf
  geänderte Gästekarte und jeden geänderten Aufenthalt (Haus, Einheit, Zeitraum, Status, Betrag — keine Meldedaten) und übernimmt, was die Kartei zu den Gästen
  dieser Seite weiß. Die Seite hält eine **Kopie** ihrer Gäste: eine Anfrage geht auch, wenn die führende Seite nicht erreichbar ist; was nicht durchging,
  wartet (die Marke rückt erst nach einem gelungenen Lauf vor). „Verbindung prüfen“ sagt, was fehlt (Schlüssel, Recht, Adresse).
- **Je Feld gewinnt die spätere Änderung** (`guests.stamps`: Name, Telefon, Land, Sprache, Notiz, gemerkter Check-in je mit Zeitstempel); Karten von vor dem
  Verbund (ohne Stempel) füllen nur leere Felder. Meldedaten reisen nur, wenn der Gast sie beim Check-in „merken“ ließ — und dann nur die bekannten Spalten.
- Abrufe nur über https an öffentliche Adressen (dieselbe Prüfung wie beim iCal-Abruf). Die Gästekarte zeigt „Auf anderen Seiten“ (führend: die gemeldeten
  Aufenthalte; anhängend: live aus der Kartei). Löschen eines Gastes gilt für die Seite, auf der es geschieht. Datenschutz-Hinweis im Admin (gemeinsame Verarbeitung).

### Check-in, Gästemails, Reinigung (B5)

- **Online-Check-in** `/buchung/gast/{schlüssel}/checkin` (bestätigte Buchung, bis zur Anreise änderbar): je Person die Felder des Landes — Kroatien/eVisitor
  (Geschlecht, Wohnland/-ort, Ausweis), Deutschland/Österreich/Meldeschein (Adresse des Hauptgastes), Italien/Alloggiati (Geburtsort, Ausweis), sonst „einfach“;
  Einstellung „Meldedaten“ (nach Land oder fest). Dazu Ankunftszeit und Anreise (Auto/Kennzeichen …). „Für meinen nächsten Aufenthalt merken“ legt die Angaben
  — und NUR dann — in die Gästekarte (`guests.checkin`), der nächste Check-in ist vorausgefüllt.
- **Admin**: Karte „Online-Check-in“ an der Buchung (Personen, Ankunft, CSV, Meldeschein-PDF, gesendete Gästemails), Seite **Meldungen** (Anreisen eines Zeitraums mit
  Check-in-Stand, alle als CSV für die Meldung). Formelschutz in jeder CSV.
- **Löschfrist**: täglicher Job löscht die Personen-Angaben N Monate nach der Abreise (Einstellung, Standard 12) — die Buchung bleibt, im Verlauf steht es.
- **Gästeseite**: Karte „Online-Check-in“ (Knopf/erledigt) und „Ihre Anreise“: Anfahrt und Hausregeln immer, **Zugang (Code/Schlüsselbox) und WLAN erst ab dem Tag vor der Anreise**.
- **Gästemails** (Job stündlich, je Buchung jede höchstens EINMAL — die Schlüssel in `bookings.mails` werden VOR dem Senden geschrieben; einzeln abschaltbar;
  eine abgeschaltete gilt als erledigt, Einschalten schickt nichts Altes nach): Check-in-Link N Tage vorher (mit Anfahrt, Hausregeln), Zugang + WLAN am Tag davor,
  Abreise-Hinweise am Abreisetag ab 7 Uhr, Danke (mit Bewertungslink, wenn eingestellt) 2–14 Tage danach. `BookingGuestMails.Due` ist die reine Regel.
- **Reinigung**: die Wechselliste ergibt sich aus den belegten Nächten — eine Buchung ODER Portal-Reservierung, die an einem Tag endet, ist eine Reinigung
  (Eigennutzung/Renovierung nicht; die „ganze Villa“ wird als ihre Apartments gereinigt). Mit nächster Anreise (Personen, Extras), „Wechsel bis 15:00“ bei
  Anreise am selben Tag, Zuständige (Einstellung, ein Name je Zeile), Erledigt, PDF mit den Checklisten der Einheiten, geheimer Kalender fürs Team
  (`/buchung/reinigung/{token}.ics`, ohne Gästenamen).
- Neu an Objekt/Einheit: Anfahrt, Hausregeln, Hinweise zur Abreise; Zugang, WLAN, Reinigungs-Checkliste.

## Stand davor: Phase B4 (0.4)

### Kalender-Abgleich mit den Portalen (B4)

- **Admin → Buchungen → Kanäle**: je Einheit die eigene Kalender-Adresse und die verbundenen Portale (Airbnb, Booking.com, Vrbo, Rentlio, Google Kalender, andere)
  mit Stand, Zahl der Belegungen, Fehler und Überschneidungen; „Jetzt lesen“, „Entfernen“ (gibt die Nächte frei), „Neue Adressen erzeugen“ (alle alten tot).
  Ein Kalender kann statt einer Adresse auch einmal eingefügt werden (Inhalt einer .ics-Datei).
- **Ausgang** `/buchung/ical/{token}.ics`: der Token der Einheit = alle belegten Nächte (Buchungen, haltende Anfragen, Sperren, andere Portale, bei der
  ganzen Villa auch die ihrer Apartments) samt Puffertagen, ohne Namen und Preise, einen Monat zurück bis drei Jahre voraus. Der Token eines PORTALS = dasselbe
  ohne die Nächte, die von diesem Portal kamen — so bekommt Airbnb seine eigenen Reservierungen nicht als fremde Sperre zurück.
- **Eingang**: Job alle 15 Minuten. Abruf nur https, nur öffentliche Adressen (geprüft auf der aufgelösten IP IM Verbindungsaufbau, auch nach jeder
  Weiterleitung — kein Weg nach localhost, 10.x, 169.254.x, fc00::/7 …), 2 MB, 20 s, drei Weiterleitungen. Gelesen wird nachsichtig (gefaltete Zeilen,
  DATE und DATE-TIME, ohne Ende = eine Nacht, STATUS:CANCELLED übersprungen). Die Sperren des Portals werden in EINER Transaktion ersetzt — was das Portal
  storniert, wird hier frei. Ein fehlgeschlagener Abruf lässt die letzten Sperren stehen.
- **Überschneidung**: eine Nacht, die hier schon belegt ist, wird nicht überschrieben; die Sperre wird mit den freien Nächten und einem Vermerk geschrieben,
  rot im Belegungsplan, als Hinweis auf dem Dashboard und einmal je neuem Konflikt per Mail (Vorlage „Doppelbuchung“) an „Neue Anfragen an“. Zwei Ereignisse
  DESSELBEN Kalenders am selben Tag sind kein Konflikt (Google führt manche Feiertage doppelt).
- Geprüft gegen einen echten Kalender (Googles Feiertage, 462 Ereignisse) im Testcontainer.

## Stand davor: Phase B3 (0.3)

### Admin-Werkzeuge (B3)

- **Belegungsplan** (Admin → Buchungen → Belegungsplan): Einheiten × Nächte eines Monats, Buchungen als Balken nach Status (Anfrage/Angebot/bestätigt),
  Sperren schraffiert, die „ganze Villa“ zeigt „Teil belegt“, solange ein Apartment belegt ist; eine Buchung der ganzen Villa steht in jeder Apartment-Zeile
  („über Ganze Villa“). Ein Klick auf einen freien Tag öffnet „Neue Buchung“ mit Einheit und Anreise. Nur Admin-Variablen (hell/dunkel).
- **Zahlungen**: Plan je bestätigter Buchung — Anzahlung (Einstellung %, Standard 30, auf ganze Euro aufgerundet) fällig N Tage nach der Bestätigung,
  Rest N Tage vor Anreise; liegt die Anreise zu nah, ist alles auf einmal fällig (`BookingPayments.Plan`, rein). Eingänge eintragen/entfernen (Betrag, Tag,
  Art, Notiz), Stand offen/teilweise/angezahlt/bezahlt in der Liste. **Erinnerung**: ein Job alle 6 Stunden schickt EINE Mail je Fälligkeit, zwei Tage
  danach (gemerkt in `reminded_deposit/rest`), abschaltbar.
- **Buchungsbestätigung als PDF** (Admin und Gastseite `/buchung/gast/{schlüssel}/pdf`, erst ab „bestätigt“): Aufenthalt, Preis, Zahlungsplan, Bezahltes,
  Zahlungshinweis — auf dem Briefkopf aus „Druckvorlage (PDF)“ (Logo, Zeilen rechts oben, Fußzeile, Farbe; leer = Name und Logo der Website).
  `PdfWriter`/`PrintLayout` sind Kopien aus dem Verein-Plugin (ein natives Plugin nutzt keine fremden Typen).
- **Gäste**: Kartei (Aufenthalte, zuletzt, Umsatz), Gästekarte mit Notiz und allen Aufenthalten; „Daten löschen“ erst ohne offene/kommende Buchung —
  dann werden die Aufenthalte anonymisiert (bleiben für die Buchhaltung) und ihre Links ungültig.
- „Heute“ ist überall mitteleuropäische Zeit (`B.Today`), nicht UTC.

## Stand davor: Phase B2 (0.2)

### Website und Anfragen (B2)

- **Blöcke**: „Buchungsanfrage“ (Unterkunft, Zeitraum, Personen, wählbare Extras, Kontakt, Einwilligung; der Preis erscheint live über `/buchung/preis`,
  `?einheit=` wählt vor), „Belegungskalender“ (2–12 Monate, frei/belegt; zwei Klicks füllen An- und Abreise im Formular derselben Seite — eine belegte
  Nacht dazwischen geht nicht, ein belegter Tag als Abreise schon) und „Unterkünfte“ (Karten mit Bild, Personen, „ab … € / Nacht“, Knopf zur Anfrage).
- **Ablauf** (`BookingFlow`, die EINE Liste erlaubter Wechsel `BookingStatus.CanMove`): Anfrage (hält die Nächte, Einstellung „Anfrage hält … Stunden“, Standard 48)
  → Angebot (mit Nachlass/Aufschlag und Text, gilt N Tage) → bestätigt; oder direkt bestätigt; abgelehnt, abgelaufen, storniert geben die Nächte frei — in
  derselben Transaktion. Ein Job (alle 15 Min.) lässt Unbeantwortetes ablaufen und schreibt dem Gast.
- **Gastseite** `/buchung/gast/{schlüssel}`: Stand, Rechnung, „Verbindlich buchen“ beim Angebot (mit Antiforgery-Token), Zahlungshinweis nach der Bestätigung;
  `noindex`. Gespeichert werden nur Hashes der Schlüssel (Tabelle `keys`); jede Mail bekommt einen frischen, alle bleiben gültig.
- **Admin → Anfragen & Buchungen**: Reiter Offen/Kommend/Vergangen/Alle, Zähler im Menü, Hinweis „neue Buchungsanfrage“ auf dem Dashboard; eine Buchung mit
  Angebot schicken / Bestätigen / Ablehnen / Stornieren, interner Notiz und Verlauf (jeder Wechsel, jede Mail — auch „nicht verschickt, weil …“).
  **Neue Buchung** (Telefon): sofort bestätigt, darf Mindestnächte/Anreisetag brechen, nie eine belegte Nacht.
- **Mails** (Admin → Mailvorlagen, eigener Wortlaut): Anfrage erhalten, neue Anfrage (an „Neue Anfragen an“), Angebot, Bestätigung, Absage, abgelaufen,
  Stornierung, Angebot angenommen. Nummern lückenlos je Anreisejahr (`2027-0001`).
- **Gästekartei** (`guests`, eindeutig nach E-Mail) wird bei jeder Anfrage gepflegt — die Admin-Seite dazu kommt mit B3.

### Grundlagen (B1)

- **Objekte** (Haus, Villa): Adresse, Land, Check-in-/Check-out-Zeit.
- **Einheiten** (Apartment, Zimmer, ganzes Haus): Personen im Preis / höchstens, Schlafzimmer, Titelbild, Text, Grundpreis je Nacht mit
  Mindestnächten und Personenaufschlag, Puffertage (Reinigung) und **„Ganzes Haus“** — eine Einheit, die andere enthält: ist eines ihrer
  Apartments belegt, ist sie belegt, und umgekehrt.
- **Saisons** je Objekt mit **Rang** (überschneiden sich zwei, gewinnt der höhere: Silvester liegt in der Nebensaison) und je Einheit ein Preis:
  je Nacht, Mindestnächte, erlaubte Anreisetage (Sa, So, Sa/So …), Wochenpreis (7 Nächte am Stück in derselben Saison), Aufschlag je Person über der
  Grundbelegung. „Ins nächste Jahr kopieren“ nimmt die Preise mit.
- **Gebühren und Kurtaxe**: je Aufenthalt, Nacht, Person oder Person und Nacht; Pflicht oder wählbar; für alle Einheiten oder eine. Die Kurtaxe ist
  eine eigene Zeile, Kinder zahlen voll, die Hälfte oder nichts.
- **Preis prüfen** (Admin) und **`GET /buchung/preis`** (öffentlich, JSON): dieselbe Rechnung, mit dem Grund, wenn ein Aufenthalt nicht passt
  („Hauptsaison: Anreise nur Sa“, „mindestens 7 Nächte“, „höchstens 4 Personen“), und ob er frei ist.
  `?einheit=<slug|nr>&anreise=JJJJ-MM-TT&abreise=JJJJ-MM-TT&erwachsene=2&kinder=0&extras=<gebühr-nr,…>`
- **Sperren** je Einheit (Eigennutzung, Renovierung): „bis“ ist der Tag, an dem wieder frei ist — wie eine Abreise.

## Regeln, die man leicht bricht

- **Deutsch ohne Kulturdaten**: das Image läuft mit invarianter Globalisierung, dort formatiert „de-DE“ still englisch („523.50“, „January“). Geld, Monate
  und Wochentage schreibt `B` selbst — nie `ToString(…, CultureInfo.GetCultureInfo("de-DE"))`.
- **Geld in ganzen Cent**, Preise brutto; der Server rechnet, ein Formular schickt nie einen Preis. Eingaben wie „1.300“ (= 1300 €), „1,50“,
  „1,200.50“ liest `B.ParseMoney` (das letzte Trennzeichen mit 1–2 Ziffern dahinter ist das Komma).
- **Nacht für Nacht** (`Pricing.Calculate`, rein, ohne Datenzugriff): Saison mit dem höchsten Rang (Gleichstand: die kürzere, dann die spätere);
  Mindestnächte und Anreisetag nach der Saison der **Anreisenacht**. Personen werden für Gebühren in **halben** Personen gezählt (Kinder „zur Hälfte“),
  ein halber Cent am Ende wird aufgerundet.
- **Doppelbuchung verhindert die Datenbank**: jede belegte Nacht ist eine Zeile in `nights` mit eindeutigem (`unit_id`, `night`). `HoldAsync` beginnt
  die Transaktion mit einem Schreibzugriff (Zeile `__lock` in `meta`, beim Laden angelegt), prüft, schreibt — eine parallele zweite scheitert am Index.
- **Eine Belegung schreibt für sich und die Einheiten, die sie ENTHÄLT** — nie für die, die sie enthalten (`BookingStore.Occupies`). Sonst sähe ein
  belegtes Apartment über das Haus auch das Nachbar-Apartment belegt. Das Haus prüft die Zeilen seiner Apartments.

## Tests

`dotnet test tests/MatCMS.Plugins.Tests --filter BookingTests` — die ganze App im Speicher, Einrichtung über die Admin-Formulare, Preise über
`/buchung/preis`: Nebensaison, Wochenpreis + Aufschlag, Anreisetag und Mindestnächte, Silvester über den Jahreswechsel, zwei Saisons in einem
Aufenthalt, Kinder-Kurtaxe, wählbare Gebühr, zu viele Gäste, Apartment ↔ ganzes Haus in beide Richtungen, sechs gleichzeitige Sperren → genau eine,
Saisons ins nächste Jahr kopieren. `BookingFlowTests`: Anfrage über das Formular (Einwilligung, Honigtopf, belegt, Regeln), Gastseite, Angebot mit
Nachlass und Annahme über den Link (ohne Token abgewiesen), Ablehnen/Stornieren geben frei, Ablauf durch den Job, Telefonbuchung, die drei Blöcke.
`BookingB3Tests`: Anzahlung aufgerundet + Rest, Zahlungen → angezahlt/bezahlt, kurzfristig = alles sofort, Erinnerung genau einmal, PDF für Admin und Gast
(nicht vor der Bestätigung), Gästekartei und Löschen, Belegungsplan. `BookingIcalTests`: Ausgang ohne Gastdaten, Portal-Adresse ohne seine eigenen
Nächte, eingefügter Kalender sperrt + meldet Überschneidung + neue Fassung ersetzt die alte, nachsichtiges Lesen, Abweisung von http/localhost/127.0.0.1/10.x/::1/169.254.169.254,
neue Adressen machen die alten ungültig. `BookingB5Tests`: Check-in mit eVisitor-Feldern (Fehler je Person, CSV, PDF, „merken“ füllt den nächsten vor),
Zugang erst ab dem Vortag, jede Gästemail einmal und nur in ihrem Fenster, Reinigungsliste aus Buchungen + Airbnb samt Teamkalender, Löschfrist.
`BookingFederationTests` (zwei ganze Seiten in einem Test): Gäste und Aufenthalte wandern, spätere Änderung gewinnt je Feld, führende Seite weg = Buchung geht, Abgleich später, API nur mit Personendaten-Recht und nur auf der führenden Seite, gemerkter Check-in nur mit bekannten Spalten.

## Nächste Phasen

Online-Zahlung (Stripe/PayPal), Rentlio-API, Gutscheine, Bewertungen auf der Seite; die Verbindung im Verbund per Cloud einrichten (Schlüssel auf Knopfdruck).
