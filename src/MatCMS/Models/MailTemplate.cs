namespace MatCMS.Models;

/// <summary>
/// The text of one kind of e-mail the site sends, editable instead of compiled in.
/// <para>Until now the only mail MatCMS sent had its German wording built in the middle of the form
/// handler, which meant a site could not change a word of what its visitors' notifications say
/// without a new release.</para>
/// </summary>
public class MailTemplate
{
    public int Id { get; set; }

    /// <summary>
    /// What this mail IS, e.g. <c>form.submission</c>. The identity everywhere: the code asks for a
    /// key, a rollout matches on the key, an import replaces by key. Never renamed once shipped —
    /// a site that customised a template would silently fall back to the built-in text.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>What the list shows. Free text; the key is what identifies it.</summary>
    public string Name { get; set; } = "";

    /// <summary>One line saying when this mail goes out.</summary>
    public string Description { get; set; } = "";

    public string Subject { get; set; } = "";

    /// <summary>Plain-text body. Placeholders are <c>{{name}}</c>, same syntax as everywhere else in
    /// MatCMS, so nobody has to learn a second one.</summary>
    public string Body { get; set; } = "";

    /// <summary>
    /// Off means the mail is not sent at all.
    /// <para>Deliberately not "delete the template": the code still asks for the key, so a missing
    /// row would either resurrect the built-in text or throw. A switch says what actually happens.</para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
