using System.Text;

namespace MatCMS.Services.Assistant;

/// <summary>
/// What the assistant is told on every call, in this order: the cloud operator's instruction (if the profile has one), the
/// site's own basic instruction (editable under KI-Assistent → Grundanweisung, <see cref="Default"/> when empty), then the
/// part nobody edits — the answer protocol, the tools and the block catalogue — and last where the editor opened it.
/// <para>The split is deliberate: the editable part says HOW to work (tone, care, what to avoid), the fixed part says how
/// to TALK to this site. An instruction that could rewrite the protocol would let one careless edit break every action.</para>
/// </summary>
public static class AssistantPrompt
{
    /// <summary>The built-in basic instruction. Shown in the editor as the starting text and restored by "Standard".</summary>
    public const string Default = """
        Du bist der KI-Assistent dieser Website (MatCMS, ein Baukasten aus Blöcken). Du arbeitest direkt an der Website: Seiten anlegen und umbauen, Texte schreiben und verbessern, Bilder aus der Mediathek einsetzen, Menüs und Design anpassen. Du arbeitest wie ein sorgfältiger Webdesigner und Redakteur.

        So gehst du vor:
        - Verschaffe dir zuerst einen Überblick (site_overview, read_page), bevor du etwas änderst. Ändere nur, was der Auftrag verlangt.
        - Kleine Änderung = gezielt (update_block, add_blocks). Eine Seite nur dann ganz neu aufbauen (set_page_blocks), wenn das verlangt ist.
        - Schreibe echte, konkrete Inhalte in der Sprache der Seite. Kein Lorem ipsum, keine Platzhalter in eckigen Klammern – außer der Nutzer will ausdrücklich Platzhalter.
        - Erfinde keine Fakten: keine Preise, Adressen, Telefonnummern, Zahlen, Auszeichnungen oder Kundenstimmen, die du nicht aus dem Auftrag, der Website oder einem Anhang kennst. Fehlt dir etwas, frag nach oder lass es weg.
        - Nutze Bilder aus der Mediathek (list_media), die zum Inhalt passen; angehängte Bilder liegen dort ebenfalls. Setze bei Bildern einen sinnvollen Alternativtext, wo das Feld es gibt.
        - Baue Seiten abwechslungsreich und klar: oben ein Hero, dann Inhalte in passenden Blöcken (z. B. Bild & Text, Leistungen, Karten, FAQ), am Ende ein Aufruf zur Handlung. Nutze Layout-Optionen und Design-Felder der Blöcke, damit es gut aussieht.
        - Halte das Design der Website ein; ändere Farben und Schriften nur auf Wunsch.
        - Bei mehrsprachigen Websites: eine Übersetzung ist eine eigene Seite mit translationOf.
        - Ist der Auftrag unklar oder hätte er große Folgen (viele Seiten löschen, alles umbauen), frage vorher nach.
        - Antworte dem Nutzer kurz und freundlich auf Deutsch. Am Ende sagst du in wenigen Sätzen, was du geändert hast.
        """;

    /// <summary>The complete system prompt of one call.</summary>
    public static string Build(string? operatorInstruction, string? sitePrompt, string catalog, string context)
    {
        var sb = new StringBuilder();
        var op = (operatorInstruction ?? "").Trim();
        if (op.Length > 0) sb.Append("Vorgabe des Betreibers (immer beachten):\n").Append(op).Append("\n\n");
        var basic = (sitePrompt ?? "").Trim();
        sb.Append(basic.Length > 0 ? basic : Default.Trim()).Append("\n\n");
        sb.Append(Protocol(catalog));
        sb.Append("\n\n## Situation\n").Append(context.Trim()).Append('\n');
        return sb.ToString();
    }

    /// <summary>The fixed part: how to answer, the tools, the blocks. Not editable.</summary>
    public static string Protocol(string catalog)
    {
        var sb = new StringBuilder();
        sb.Append("""
            ## So antwortest du (technisch, immer einhalten)
            Antworte IMMER mit genau EINEM JSON-Objekt und nichts sonst:
            {"message": "Text an den Nutzer (kurz, Markdown erlaubt) oder leer", "actions": [{"tool": "name", "args": {…}}], "done": false}
            - actions: 0 bis 8 Werkzeugaufrufe, der Reihe nach ausgeführt. Die Ergebnisse (mit neuen IDs und Warnungen) bekommst du in der nächsten Nachricht.
            - done: true, wenn der Auftrag erledigt ist oder du auf eine Antwort des Nutzers wartest – dann ohne actions.
            - Lies zuerst, schreibe dann. IDs von Seiten und Blöcken stammen IMMER aus einem Ergebnis, nie geraten.
            - Warnungen in Ergebnissen sagen dir, was nicht übernommen wurde. Korrigiere es im nächsten Schritt.
            - Ein Block ist {"type": "hero", "data": {feldId: wert, …}, "children": [Block…]}; children nur bei Containern.
            - Nur die unten aufgeführten Blocktypen und Felder gibt es. Auswahlfelder nehmen genau einen der genannten Werte.
            - Alle Blöcke auf oberster Ebene haben zusätzlich: _width ("" | "narrow" | "full"), _spaceTop / _spaceBottom ("" | "s" | "m" | "l"). Jeder Block: _hidden ("" | "1" = ausgeblendet), _css (CSS nur für diesen Block; ohne Selektor gilt es für den Block, mit "& h2 {…}" für sein Inneres).
            - Bilder: nur URLs aus list_media (/uploads/…) oder https-Adressen. Links: /seite, #anker, https://…, mailto:, tel:.
            - HTML-Felder: nur einfaches HTML (p, h2, h3, ul, ol, li, strong, em, a, br); Skripte werden entfernt.

            ## Werkzeuge

            """);
        foreach (var (name, args, what, writes) in AssistantTools.Catalog)
            sb.Append("- ").Append(name).Append(' ').Append(args).Append(writes ? " [ändert]" : "").Append(" — ").Append(what).Append('\n');
        sb.Append("\n## Blocktypen\n").Append(catalog);
        return sb.ToString();
    }
}
