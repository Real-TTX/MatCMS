# MatCMS License

Version 1.0 · 9 November 2026

Copyright © 2026 Matthias Schmoldt, Brombergweg 11, 74392 Freudental, Germany
("the Licensor"). Contact: kontakt@matcms.de · https://matcms.de

MatCMS is **source-available, not open source**. You may read, run and change the
code under the terms below. Personal and non-profit use is free. Commercial use needs
a paid licence per website.

A short summary in German follows at the end of this document. Only the English text
is binding.

---

## 1. What this licence covers

"The Software" means everything in this repository, in particular:

- MatCMS (`src/MatCMS`),
- MatCMS Cloud (`src/MatCMS.Cloud`),
- the shared libraries (`src/MatCMS.Shared`, `src/MatCMS.Shared.Web`, `src/MatCMS.Plugin.Abstractions`),
- the plugins in `plugins/`,
- the templates, documentation and brand files,
- the container images built from all of this (`ghcr.io/real-ttx/matcms`, `ghcr.io/real-ttx/matcms-cloud`).

Third-party components keep their own licences. See section 9.

## 2. Definitions

- **Instance**: one running installation of MatCMS with its own database. One Instance
  serves one website. That website may have any number of languages and of domains that
  point to the same content.
- **Production Instance**: an Instance that is reachable by the public or by its
  intended users. An Instance used only for development, testing, staging or backups is
  not a Production Instance.
- **Personal Use**: use by a natural person for private purposes without a commercial
  aim. Examples: a personal homepage, a private blog, a family or hobby website, a
  wedding page, a portfolio that does not offer paid services.
- **Non-Profit Use**: use by or for one of the following:
  - an organisation recognised as charitable or non-profit (for example a German
    *gemeinnütziger Verein* or *gemeinnützige GmbH*);
  - a registered association that does not pursue a commercial purpose;
  - a school, university or other public educational institution.

  In every case, the website must serve that organisation's own non-commercial purpose.
- **Commercial Use**: any use that is neither Personal Use nor Non-Profit Use. In
  particular, this means use by or for a company, freelancer, self-employed person, shop,
  agency or public authority, or for a website that offers, advertises or sells goods or
  services.

## 3. Free use

For Personal Use and Non-Profit Use, the Licensor grants you a free, worldwide,
non-exclusive, non-transferable licence. It allows you to:

1. download, install and run the Software on any number of Instances;
2. read the source code and change it for your own use;
3. build your own plugins, templates and components for it.

## 4. Commercial use

1. **A Commercial Licence is required for each Production Instance used commercially.**
   The price is listed at https://matcms.de/preise. When this licence was published, it
   was **9 € per month or 99 € per year per Production Instance**. The Licensor is a
   small business under § 19 UStG, so no VAT is charged.
2. **Evaluation**: you may try the Software commercially for 30 days without a
   Commercial Licence.
3. **No Commercial Licence is needed for Instances that are not Production Instances**,
   such as development, testing or staging.
4. **Agencies and freelancers** may build and maintain websites for their clients. Each
   commercially used Production Instance still needs a Commercial Licence. Either the
   client or the agency may hold it.
5. **MatCMS Cloud** may be used commercially without an extra fee to manage, update and
   back up Instances that are themselves licensed or free under section 3. Offering the
   Software or MatCMS Cloud as a hosted service to third parties needs a separate
   written agreement with the Licensor. This includes website hosting, "MatCMS as a
   service" and reselling Instances.
6. A Commercial Licence is obtained by contacting the Licensor (kontakt@matcms.de). It
   becomes effective once the Licensor confirms it in writing, which includes email.

## 5. Changes and distribution

1. You may change the Software for your own use under section 3 or 4.
2. You may share the source code of the Software, changed or unchanged, for example as
   a fork on GitHub, under the following conditions:
   - this licence goes with it;
   - all copyright and licence notices stay in place;
   - any change you made is clearly marked as such.
3. You may not sell the Software, or a product mainly based on it, or offer it as a
   competing content management system or hosting product. A separate written agreement
   with the Licensor is the only exception.
4. You may not remove or hide the licence terms in order to avoid section 4.

## 6. Your own work

Plugins, templates, components and content that you create yourself belong to you. You
may license them in any way you choose. This includes plugins written only against
`MatCMS.Plugin.Abstractions`. Your website's content is never covered by this licence.

## 7. Contributions

By sending a contribution (pull request, patch or other code) to the Licensor, you
confirm that you have the right to do so. You also grant the Licensor a perpetual,
worldwide, free and irrevocable right to use, change, sublicense and distribute your
contribution as part of the Software, under this licence or any other.

## 8. Ending the licence

If you breach this licence, your rights under it end automatically. They come back if
you fix the breach within 30 days after the Licensor tells you about it. This applies to
the first breach only.

## 9. Third-party components

The Software includes components by other authors under their own licences. Examples
are CodeMirror, Tabler Icons, Cropper.js, modern-screenshot, qrcode, ImageSharp, PdfPig
and the .NET libraries referenced by the projects. Their licence files and notices are
in the respective folders or packages. Those licences take precedence for their
components.

## 10. Warranty and liability

The Software is provided **"as is", without any warranty**, to the extent the law
allows.

The Licensor is liable without limitation for intent and gross negligence, for injury to
life, body or health, and under the German Product Liability Act
(*Produkthaftungsgesetz*). For slight negligence, the Licensor is liable only for breach
of an essential contractual obligation, and only up to the damage typical and
foreseeable for this kind of contract. For free use under section 3, liability is
limited to intent and gross negligence (§§ 521, 599 BGB by analogy).

## 11. Governing law

This licence is governed by the law of the Federal Republic of Germany. The UN
Convention on Contracts for the International Sale of Goods does not apply. If you are a
merchant, the place of jurisdiction is the Licensor's place of business.

---

## Kurzfassung (Deutsch, unverbindlich)

- **Privat und gemeinnützig: kostenlos.** Das gilt für die eigene Website, den Blog,
  Vereine, Schulen und Non-Profits.
- **Unternehmen: 9 € pro Monat oder 99 € pro Jahr je Website** (je produktiver Instanz).
  Als Kleinunternehmer nach § 19 UStG wird keine Umsatzsteuer berechnet.
- **Was keine Lizenz braucht:** Test-, Entwicklungs- und Staging-Instanzen sowie 30 Tage
  Ausprobieren.
- **Agenturen:** Sie dürfen Websites für Kunden bauen. Jede gewerblich genutzte Website
  braucht eine Lizenz.
- **Was eine eigene Vereinbarung braucht:** MatCMS oder die Cloud als Hosting-Dienst
  für andere anbieten.
- **Quellcode und Änderungen:** Der Code darf gelesen und für den eigenen Gebrauch
  geändert werden. Weiterverkaufen darf man ihn nicht.
- **Eigene Arbeit:** Eigene Plugins, Vorlagen und Inhalte gehören Ihnen.
- **Lizenz kaufen:** kontakt@matcms.de oder https://matcms.de/preise
