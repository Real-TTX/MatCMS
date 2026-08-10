namespace MatCMS.Cloud.Pages.Shared;

/// <summary>
/// What the shared mail-body block needs: the key it is for, and the text.
/// <para>The key is what decides which placeholders and loops are offered — they come from the
/// declaration in <c>MatCMS.Shared.MailTemplates</c>, which is the same list the CMS fills in.</para>
/// </summary>
public sealed record MailTemplateFields(string Key, string Body);
