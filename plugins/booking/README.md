# Buchungen (natives Plugin)

Ferienhäuser, Villen und Apartments für eine MatCMS-Seite, gebaut nur gegen `MatCMS.Plugin.Abstractions`. Key `booking`, Admin unter
Admin → Buchungen. Entwurf und Phasen: [`docs/booking-plugin.md`](../../docs/booking-plugin.md). Erste Seiten: Villa Nika, Villa Sogi.

## Stand: Phase B2 (0.2)

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

## Nächste Phasen

B3 Belegungsplan, Gästekartei, Zahlungen, Bestätigungs-PDF ·
B4 iCal (Airbnb, Booking.com, Rentlio) · B5 Online-Check-in, Mails, Reinigung · B6 Kundenverbund.
