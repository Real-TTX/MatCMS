# Buchungen (natives Plugin)

Ferienhäuser, Villen und Apartments für eine MatCMS-Seite, gebaut nur gegen `MatCMS.Plugin.Abstractions`. Key `booking`, Admin unter
Admin → Buchungen. Entwurf und Phasen: [`docs/booking-plugin.md`](../../docs/booking-plugin.md). Erste Seiten: Villa Nika, Villa Sogi.

## Stand: Phase B1 (0.1)

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
Saisons ins nächste Jahr kopieren.

## Nächste Phasen

B2 Website (Belegungskalender, Anfrage mit Live-Preis, Unterkünfte) und Anfrage → Angebot → Bestätigung · B3 Belegungsplan, Buchungen, Gäste ·
B4 iCal (Airbnb, Booking.com, Rentlio) · B5 Online-Check-in, Mails, Reinigung · B6 Kundenverbund.
