using MatCMS.Plugin;

namespace MatCMS.Plugin.Bot;

/// <summary>The bot's texts, German and English in one row (like the shop's), so neither language can be forgotten. The widget's words are read by
/// language straight from this table (<see cref="For"/>): the widget asks one address for every language of the site.</summary>
internal static class BotStrings
{
    public static void Register(IPluginBuilder b)
    {
        b.Strings("de", All.ToDictionary(r => r.Key, r => r.De));
        b.Strings("en", All.ToDictionary(r => r.Key, r => r.En));
    }

    /// <summary>A text in a language: German for "de", English for every other one (the site's further languages have no table of their own).</summary>
    public static string For(string lang, string key) =>
        All.FirstOrDefault(r => r.Key == key) is var r && r.Key is not null ? (lang == "de" ? r.De : r.En) : key;

    internal static readonly (string Key, string De, string En)[] All =
    {
        // ---- back office -------------------------------------------------------------------------------
        ("bot.nav", "AI Bot", "AI bot"), ("action.search", "Suchen…", "Search…"),
        ("bot.tab.overview", "Übersicht", "Overview"), ("bot.tab.chats", "Verläufe", "Conversations"),
        ("bot.ov.title", "AI Bot", "AI bot"),
        ("bot.ov.sub", "Ein Chat-Assistent für die Besucher dieser Website. Er antwortet aus den veröffentlichten Inhalten und – wenn erlaubt – aus den Daten des angemeldeten Kunden.",
            "A chat assistant for the visitors of this site. It answers from the published content and – where allowed – from the signed-in customer's own data."),
        ("bot.ov.aiOff", "Für diese Website ist keine KI eingerichtet. Der Bot antwortet erst, wenn unter Einstellungen → KI eine Verbindung aktiv ist (eigenes Profil oder die Cloud).",
            "This site has no AI set up. The bot answers once a connection is active under Settings → AI (an own profile or the cloud)."),
        ("bot.ov.aiOn", "KI-Verbindung aktiv.", "AI connection active."),
        ("bot.ov.memberOn", "Kundendaten: angemeldete Kunden bekommen Antworten zu ihren eigenen Bestellungen. Diese Daten gehen an den KI-Anbieter – bitte in der Datenschutzerklärung nennen.",
            "Customer data: signed-in customers get answers about their own orders. That data goes to the AI provider – please name it in the privacy statement."),
        ("bot.ov.memberOff", "Kundendaten: aus. Der Bot sieht nur öffentliche Inhalte.", "Customer data: off. The bot sees public content only."),
        ("bot.ov.bubbleOff", "Die Chat-Blase ist ausgeschaltet. Der Bot erscheint nur dort, wo der Block „AI Bot“ auf einer Seite steht.",
            "The chat bubble is off. The bot only appears where the block “AI bot” stands on a page."),
        ("bot.ov.today", "Fragen heute", "Questions today"), ("bot.ov.tokensToday", "Tokens heute", "Tokens today"),
        ("bot.ov.month", "Tokens diesen Monat", "Tokens this month"), ("bot.ov.ofLimit", "von {0}", "of {0}"), ("bot.ov.noLimit", "ohne Grenze", "no limit"),
        ("bot.ov.settings", "Einstellungen", "Settings"), ("bot.ov.try", "Auf der Website ansehen", "View on the site"),
        ("bot.chats.off", "Verläufe werden nicht gespeichert. Unter Einstellungen → Verlauf lässt sich eine Aufbewahrungsdauer einstellen.",
            "Conversations are not stored. A retention period can be set under Settings → History."),
        ("bot.chats.kept", "Verläufe werden {0} Tage aufbewahrt und danach automatisch gelöscht.", "Conversations are kept for {0} days and then deleted automatically."),
        ("bot.chats.none", "Noch keine Verläufe.", "No conversations yet."),
        ("bot.chats.when", "Zeit", "Time"), ("bot.chats.who", "Besucher", "Visitor"), ("bot.chats.question", "Erste Frage", "First question"), ("bot.chats.turns", "Fragen", "Questions"),
        ("bot.chats.guest", "Gast", "Guest"), ("bot.chats.member", "Kunde #{0}", "Customer #{0}"), ("bot.chats.open", "Ansehen", "View"),
        ("bot.chats.deleteAll", "Alle Verläufe löschen", "Delete all conversations"), ("bot.chats.confirmDeleteAll", "Alle gespeicherten Verläufe endgültig löschen?", "Delete every stored conversation for good?"),
        ("bot.chats.deleted", "Verläufe gelöscht.", "Conversations deleted."), ("bot.chats.back", "Zurück zu den Verläufen", "Back to the conversations"),
        ("bot.chats.q", "Frage", "Question"), ("bot.chats.a", "Antwort", "Answer"), ("bot.chats.sources", "Quellen", "Sources"), ("bot.chats.tokens", "{0} Tokens", "{0} tokens"),
        ("bot.chats.delete", "Diesen Verlauf löschen", "Delete this conversation"), ("bot.chats.confirmDelete", "Diesen Verlauf löschen?", "Delete this conversation?"),
        ("bot.block.name", "AI Bot", "AI bot"), ("bot.block.desc", "Der Chat-Assistent als Feld auf der Seite (statt oder zusätzlich zur Chat-Blase).", "The chat assistant as a panel on the page (instead of or besides the chat bubble)."),
        ("bot.block.heading", "Überschrift", "Heading"), ("bot.block.intro", "Einleitung", "Introduction"),

        // ---- settings ----------------------------------------------------------------------------------
        ("bot.set.gLook", "Auftritt", "Appearance"),
        ("bot.set.name", "Name des Bots", "Bot's name"), ("bot.set.nameHelp", "So stellt er sich vor und so steht er im Chatfenster.", "How it introduces itself and what the chat window shows."),
        ("bot.set.greeting", "Begrüßung", "Greeting"), ("bot.set.greetingHelp", "Die erste Nachricht im Chatfenster. Leer = eine kurze Standardbegrüßung.", "The first message in the chat window. Empty = a short default greeting."),
        ("bot.set.avatar", "Bild (Avatar)", "Picture (avatar)"), ("bot.set.avatarHelp", "Rund dargestellt. Leer = die Anfangsbuchstaben des Namens.", "Shown round. Empty = the name's initials."),
        ("bot.set.useAccent", "Farbe des Templates verwenden", "Use the template's colour"), ("bot.set.useAccentHelp", "An: Der Bot nimmt die Akzentfarbe der Website. Aus: die Farbe darunter.", "On: the bot takes the site's accent colour. Off: the colour below."),
        ("bot.set.color", "Eigene Farbe", "Own colour"),
        ("bot.set.position", "Position der Chat-Blase", "Position of the chat bubble"), ("bot.set.posRight", "Unten rechts", "Bottom right"), ("bot.set.posLeft", "Unten links", "Bottom left"),
        ("bot.set.bubble", "Chat-Blase auf allen Seiten", "Chat bubble on every page"), ("bot.set.bubbleHelp", "Aus: Der Bot erscheint nur, wo der Block „AI Bot“ steht.", "Off: the bot only appears where the block “AI bot” stands."),
        ("bot.set.placeholder", "Text im Eingabefeld", "Text in the input field"),
        ("bot.set.gBehave", "Verhalten", "Behaviour"),
        ("bot.set.instruction", "Grundanweisung", "Basic instruction"),
        ("bot.set.instructionHelp", "Ton, Schwerpunkte, was er empfehlen soll, was nicht. Beispiel: „Antworte freundlich und kurz, duze die Besucher, verweise bei Terminwünschen auf /kontakt.“ Die Regeln, dass er nur aus den Website-Inhalten antwortet und nichts erfindet, gelten immer.",
            "Tone, focus, what to recommend and what not. Example: “Answer kindly and briefly, point people who want an appointment to /contact.” The rules that it answers only from the site's content and invents nothing always apply."),
        ("bot.set.fallback", "Wenn er nicht weiterweiß", "When it does not know"),
        ("bot.set.fallbackHelp", "Was er dann vorschlägt, z. B. „Schreiben Sie uns über /kontakt oder rufen Sie an: 0711 123456.“", "What it suggests then, e.g. “Write to us via /contact or call 0711 123456.”"),
        ("bot.set.sources", "Quellen unter der Antwort zeigen", "Show sources under the answer"),
        ("bot.set.gData", "Kundendaten", "Customer data"),
        ("bot.set.memberData", "Angemeldeten Kunden Auskunft über ihre eigenen Daten geben", "Answer signed-in customers about their own data"),
        ("bot.set.memberDataHelp", "Zum Beispiel ihre Bestellungen im Shop. Der Bot sieht dabei immer nur das Konto, mit dem der Besucher angemeldet ist – nie das eines anderen. Diese Daten werden an den KI-Anbieter übermittelt: in der Datenschutzerklärung nennen.",
            "For instance their orders in the shop. The bot only ever sees the account the visitor is signed in with – never another one. That data is sent to the AI provider: name it in the privacy statement."),
        ("bot.set.gLimits", "Grenzen", "Limits"),
        ("bot.set.maxAnswer", "Länge einer Antwort (Tokens)", "Length of an answer (tokens)"),
        ("bot.set.maxAnswerHelp", "Obergrenze pro Antwort. Denkende Modelle brauchen einen Teil davon zum Nachdenken – unter 800 kommen bei ihnen oft leere Antworten.", "Cap per answer. Reasoning models spend part of it thinking – below 800 they often answer empty."),
        ("bot.set.maxQuestion", "Länge einer Frage (Zeichen)", "Length of a question (characters)"),
        ("bot.set.perVisitor", "Fragen pro Besucher und Tag", "Questions per visitor and day"),
        ("bot.set.perVisitorTokens", "Tokens pro Besucher und Tag", "Tokens per visitor and day"), ("bot.set.zeroUnlimited", "0 = ohne Grenze.", "0 = no limit."),
        ("bot.set.perMonth", "Tokens pro Monat für die ganze Website", "Tokens per month for the whole site"),
        ("bot.set.perMonthHelp", "Ist die Grenze erreicht, antwortet der Bot bis zum Monatsende nicht mehr. 0 = ohne Grenze.", "Once reached, the bot stops answering until the month is over. 0 = no limit."),
        ("bot.set.limitText", "Text, wenn eine Grenze erreicht ist", "Text when a limit is reached"),
        ("bot.set.gConsent", "Einwilligung", "Consent"),
        ("bot.set.consent", "Zustimmung vor der ersten Frage verlangen", "Ask for consent before the first question"),
        ("bot.set.consentHelp", "An: Das Chatfenster zeigt zuerst den Einwilligungstext und einen Knopf „Zustimmen“; erst danach lässt sich eine Frage stellen. Der Server prüft die Zustimmung mit, und ändert sich der Text, wird erneut gefragt. Nötig vor allem, wenn der KI-Anbieter außerhalb der EU sitzt. Aus: Der Chat startet sofort – dann muss die Datenschutzerklärung die Verarbeitung anders begründen.",
            "On: the chat window first shows the consent text and an “Agree” button; only then can a question be asked. The server checks the consent too, and when the text changes visitors are asked again. Needed above all when the AI provider is outside the EU. Off: the chat starts at once – the privacy statement then has to justify the processing otherwise."),
        ("bot.set.consentTemplate", "Textvorlage", "Text template"),
        ("bot.set.consentTemplateHelp", "Nach dem Sitz des KI-Anbieters wählen. Die Vorlagen setzen Anbieter, Land und den Link zur Datenschutzerklärung ein. Kein Ersatz für eine Rechtsberatung.",
            "Choose by where the AI provider sits. The templates fill in provider, country and the link to the privacy statement. Not a substitute for legal advice."),
        ("bot.set.ct.eu", "KI-Anbieter in der EU (z. B. Mistral)", "AI provider in the EU (e.g. Mistral)"),
        ("bot.set.ct.dpf", "USA, zertifiziert nach dem Data Privacy Framework (z. B. OpenAI)", "USA, certified under the Data Privacy Framework (e.g. OpenAI)"),
        ("bot.set.ct.thirdcountry", "Drittland ohne Angemessenheitsbeschluss (z. B. China, DeepSeek)", "Third country without an adequacy decision (e.g. China, DeepSeek)"),
        ("bot.set.ct.custom", "Eigener Text", "Own text"),
        ("bot.set.consentProvider", "KI-Anbieter", "AI provider"), ("bot.set.consentProviderHelp", "Wie er im Text heißt, z. B. „DeepSeek“ oder „Mistral AI (Paris)“.", "How it is named in the text, e.g. “DeepSeek” or “Mistral AI (Paris)”."),
        ("bot.set.consentCountry", "Land des Anbieters", "Provider's country"), ("bot.set.consentCountryHelp", "Z. B. „China“, „den USA“, „Frankreich“.", "E.g. “China”, “the USA”, “France”."),
        ("bot.set.privacyUrl", "Link zur Datenschutzerklärung", "Link to the privacy statement"),
        ("bot.set.consentText", "Eigener Einwilligungstext", "Own consent text"),
        ("bot.set.consentTextHelp", "Nur bei der Vorlage „Eigener Text“. Platzhalter: {provider}, {country}, {privacy}. **fett** und [Link](/adresse) gehen.", "Only with the template “Own text”. Placeholders: {provider}, {country}, {privacy}. **bold** and [link](/address) work."),
        ("bot.set.consentButton", "Text des Knopfs", "Button text"),
        ("bot.ov.consentOn", "Einwilligung: Besucher stimmen vor der ersten Frage zu.", "Consent: visitors agree before their first question."),
        ("bot.ov.consentOff", "Einwilligung: aus – der Chat startet ohne Zustimmung.", "Consent: off – the chat starts without agreement."),
        ("bot.set.gHistory", "Verlauf", "History"),
        ("bot.set.keepDays", "Verläufe speichern (Tage)", "Store conversations (days)"),
        ("bot.set.keepDaysHelp", "0 = nichts wird gespeichert. Sonst sind die Gespräche unter AI Bot → Verläufe lesbar und werden nach dieser Zahl Tagen gelöscht. Gespeichert werden Fragen und Antworten, bei Kunden die Kundennummer – keine IP-Adresse.",
            "0 = nothing is stored. Otherwise the conversations can be read under AI bot → Conversations and are deleted after this many days. Questions and answers are stored, for customers their customer number – no IP address."),

        // ---- the widget (read by language through For) -------------------------------------------------
        ("bot.w.open", "Chat öffnen", "Open chat"), ("bot.w.close", "Schließen", "Close"), ("bot.w.send", "Senden", "Send"),
        ("bot.w.placeholder", "Ihre Frage …", "Your question …"), ("bot.w.thinking", "schreibt …", "is typing …"),
        ("bot.w.greeting", "Hallo, ich bin {0}! Wie kann ich Ihnen helfen?", "Hello, I am {0}! How can I help you?"),
        ("bot.w.greetingPlain", "Hallo! Wie kann ich Ihnen helfen?", "Hello! How can I help you?"),
        ("bot.w.error", "Das hat gerade nicht geklappt. Bitte versuchen Sie es gleich noch einmal.", "That did not work just now. Please try again in a moment."),
        ("bot.w.note", "Antworten erzeugt eine KI und können Fehler enthalten.", "Answers are written by an AI and may contain mistakes."),
        ("bot.w.noteMember", "Sie sind angemeldet: Der Assistent kann Ihre eigenen Bestellungen einsehen.", "You are signed in: the assistant can see your own orders."),
        ("bot.w.sources", "Quellen", "Sources"), ("bot.w.reset", "Neues Gespräch", "New conversation"),
        ("bot.w.limit", "Für heute sind keine weiteren Fragen möglich. Bitte nutzen Sie unser Kontaktformular.", "No more questions are possible today. Please use our contact form."),
        ("bot.w.unavailable", "Der Assistent ist gerade nicht verfügbar.", "The assistant is not available at the moment."),
        ("bot.w.agree", "Zustimmen und Chat starten", "Agree and start the chat"),
        ("bot.w.consentNeeded", "Bitte stimmen Sie zuerst der Übermittlung zu.", "Please agree to the transfer first."),
        ("bot.consent.someProvider", "einen KI-Anbieter", "an AI provider"), ("bot.consent.someCountry", "einem Land außerhalb der EU", "a country outside the EU"),
        ("bot.consent.eu",
            "Damit eine KI Ihre Frage beantworten kann, werden die Frage und der bisherige Gesprächsverlauf an {provider} übermittelt. Die Verarbeitung findet in der EU statt. Bitte geben Sie keine persönlichen Daten wie Namen, Adressen oder Telefonnummern ein. Mehr dazu in der [Datenschutzerklärung]({privacy}).",
            "So that an AI can answer your question, the question and the conversation so far are sent to {provider}. Processing takes place in the EU. Please do not enter personal data such as names, addresses or phone numbers. More in the [privacy statement]({privacy})."),
        ("bot.consent.dpf",
            "Damit eine KI Ihre Frage beantworten kann, werden die Frage und der bisherige Gesprächsverlauf an {provider} in {country} übermittelt. Der Anbieter ist nach dem EU-US Data Privacy Framework zertifiziert. Bitte geben Sie keine persönlichen Daten wie Namen, Adressen oder Telefonnummern ein. Mit „Zustimmen“ willigen Sie in diese Übermittlung ein; Sie können den Chat jederzeit wieder schließen. Mehr dazu in der [Datenschutzerklärung]({privacy}).",
            "So that an AI can answer your question, the question and the conversation so far are sent to {provider} in {country}. The provider is certified under the EU-US Data Privacy Framework. Please do not enter personal data such as names, addresses or phone numbers. By choosing “Agree” you consent to this transfer; you can close the chat at any time. More in the [privacy statement]({privacy})."),
        ("bot.consent.thirdcountry",
            "Damit eine KI Ihre Frage beantworten kann, werden die Frage und der bisherige Gesprächsverlauf an **{provider} in {country}** übermittelt. Für dieses Land gibt es keinen Angemessenheitsbeschluss der EU: Ein mit der EU vergleichbarer Datenschutz ist dort nicht gewährleistet, Behörden können auf die Daten zugreifen, ohne dass Ihnen wirksame Rechtsbehelfe zur Verfügung stehen. Bitte geben Sie deshalb keine persönlichen Daten wie Namen, Adressen oder Telefonnummern ein. Mit „Zustimmen“ willigen Sie ausdrücklich in diese Übermittlung ein (Art. 49 Abs. 1 lit. a DSGVO). Sie können den Chat jederzeit wieder schließen; für Anfragen steht auch das Kontaktformular bereit. Mehr dazu in der [Datenschutzerklärung]({privacy}).",
            "So that an AI can answer your question, the question and the conversation so far are sent to **{provider} in {country}**. There is no EU adequacy decision for this country: data protection comparable to the EU is not guaranteed there, and authorities may access the data without effective legal remedies for you. Please therefore do not enter personal data such as names, addresses or phone numbers. By choosing “Agree” you expressly consent to this transfer (Art. 49(1)(a) GDPR). You can close the chat at any time; the contact form is there for enquiries too. More in the [privacy statement]({privacy})."),
        ("bot.w.tooLong", "Die Frage ist zu lang (höchstens {0} Zeichen).", "The question is too long (at most {0} characters)."),
    };
}
