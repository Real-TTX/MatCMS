namespace MatCMS.Plugin;

// What a plugin can reach besides its own tables: the site's customer accounts, its mail, and a private place for files.
// Typed and narrow on purpose — the host's services behind them (member service, e-mail service, storage paths) can change
// without a plugin being rebuilt.

// ---- accounts ---------------------------------------------------------------------------------------------------

/// <summary>An account of the site's guest area, as a plugin sees it. Never carries the password hash.</summary>
/// <param name="Id">The key.</param>
/// <param name="Username">The login name (for an account created with an e-mail address, the address itself).</param>
/// <param name="DisplayName">The name to show.</param>
/// <param name="Email">The e-mail address, or "".</param>
/// <param name="EmailConfirmed">The owner followed the link in the confirmation mail.</param>
/// <param name="IsActive">An inactive account cannot sign in.</param>
/// <param name="Roles">The roles it holds.</param>
/// <param name="CreatedUtc">When it was created.</param>
public sealed record MemberAccount(long Id, string Username, string DisplayName, string Email, bool EmailConfirmed, bool IsActive,
    IReadOnlyList<string> Roles, DateTime CreatedUtc);

/// <summary>What came of <see cref="IPluginMembers.CreateAsync"/>.</summary>
public enum MemberCreateStatus
{
    /// <summary>The account exists now.</summary>
    Created,
    /// <summary>Another account already uses the address.</summary>
    EmailTaken,
    /// <summary>The address is not a valid e-mail address.</summary>
    InvalidEmail,
    /// <summary>The password is too short or empty.</summary>
    WeakPassword
}

/// <summary>The answer of <see cref="IPluginMembers.CreateAsync"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Account">The new account when <see cref="MemberCreateStatus.Created"/>, otherwise null.</param>
public sealed record MemberCreateResult(MemberCreateStatus Status, MemberAccount? Account);

/// <summary>
/// The site's customer accounts (the guest area behind <c>/anmelden</c>, the same accounts members-only pages check). Accounts are
/// one thing for the whole site — a shop, a forum and a course plugin all see the same people — so a plugin keeps what it knows about a
/// customer (addresses, orders) in ITS OWN table, keyed by <see cref="MemberAccount.Id"/>.
/// <para><b>Policy is the plugin's.</b> The site setting that lets visitors register on the CMS's own page does not bind a plugin that
/// creates an account for its own purpose (a shop's checkout); the plugin decides, with its own setting, whether it offers that.</para>
/// </summary>
public interface IPluginMembers
{
    /// <summary>The account with this key, or null.</summary>
    Task<MemberAccount?> FindAsync(long id, CancellationToken ct = default);

    /// <summary>The account with this e-mail address (case-insensitive), or null.</summary>
    Task<MemberAccount?> FindByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Creates an account. The address becomes the login name too. A confirmation mail goes out (the account works at once; an
    /// unconfirmed address only means <see cref="MemberAccount.EmailConfirmed"/> is false, which a plugin may use before it trusts the
    /// address — for instance to attach earlier guest orders). Raises <see cref="HostEvents.MemberRegistered"/>.
    /// </summary>
    Task<MemberCreateResult> CreateAsync(string email, string password, string displayName, CancellationToken ct = default);

    /// <summary>Signs the account in on this request (a cookie is set on the answer). Needs an HTTP request.</summary>
    Task SignInAsync(MemberAccount member, bool persistent = false, CancellationToken ct = default);

    /// <summary>Signs the current visitor out. Needs an HTTP request.</summary>
    Task SignOutAsync(CancellationToken ct = default);

    /// <summary>Changes the password after checking the current one. False when the current password is wrong or the new one too weak.</summary>
    Task<bool> ChangePasswordAsync(long id, string currentPassword, string newPassword, CancellationToken ct = default);

    /// <summary>Deletes the account and raises <see cref="HostEvents.MemberDeleted"/> and <see cref="HostEvents.MemberErase"/> so every plugin can remove or anonymise what it holds.</summary>
    Task<bool> DeleteAsync(long id, CancellationToken ct = default);
}

// ---- mail -------------------------------------------------------------------------------------------------------

/// <summary>A placeholder a mail template offers (<c>{{order_number}}</c>).</summary>
/// <param name="Token">The name inside the braces.</param>
/// <param name="Description">What it contains, shown in the template editor.</param>
public sealed record MailPlaceholder(string Token, string Description);

/// <summary>A repeatable block of a mail (<c>{{#items}} … {{/items}}</c>).</summary>
/// <param name="Name">The loop's name.</param>
/// <param name="Description">What one pass stands for.</param>
/// <param name="Fields">The placeholders valid inside the block.</param>
public sealed record MailLoop(string Name, string Description, IReadOnlyList<MailPlaceholder> Fields);

/// <summary>
/// A mail the plugin can send. It is added to <i>Admin → Mailvorlagen</i> the first time the plugin loads, so the site's operator can
/// change the wording — the default text here is only the starting point, and an operator's edit is never overwritten.
/// </summary>
/// <param name="Key">The identity, <c>plugin-key.name</c> (<c>shop.order.paid</c>). Never renamed once shipped.</param>
/// <param name="Name">The name in the list.</param>
/// <param name="Description">When this mail goes out.</param>
/// <param name="Subject">The default subject.</param>
/// <param name="Body">The default body (plain text; placeholders as <c>{{token}}</c>).</param>
public sealed record MailTemplateSpec(string Key, string Name, string Description, string Subject, string Body)
{
    /// <summary>What may be written into subject and body.</summary>
    public IReadOnlyList<MailPlaceholder> Placeholders { get; init; } = Array.Empty<MailPlaceholder>();
    /// <summary>The repeatable blocks.</summary>
    public IReadOnlyList<MailLoop> Loops { get; init; } = Array.Empty<MailLoop>();
    /// <summary>An HTML starting point for an operator who switches the template to HTML; empty offers none.</summary>
    public string HtmlBody { get; init; } = "";
}

/// <summary>The outcome of a send.</summary>
/// <param name="Ok">The mail was handed to the mail server (or the cloud's relay).</param>
/// <param name="Error">Why not, in words for the log; null when sent.</param>
public sealed record MailResult(bool Ok, string? Error);

/// <summary>Sends mail through the site's own settings (SMTP, or the cloud's relay when a profile says so).</summary>
public interface IPluginMail
{
    /// <summary>
    /// Sends the template <paramref name="templateKey"/> — the operator's edited version when there is one, the plugin's default otherwise.
    /// Never throws: a mail server that is down is a <see cref="MailResult"/> with an error, so an order is never lost because of it.
    /// </summary>
    /// <param name="templateKey">A key the plugin declared with <see cref="IPluginBuilder.MailTemplate"/>.</param>
    /// <param name="to">The recipients.</param>
    /// <param name="values">The placeholders: token → text (encoded for you when the template is HTML).</param>
    /// <param name="lists">Repeatable blocks: loop name → one dictionary per pass.</param>
    /// <param name="replyTo">An address replies go to, or null.</param>
    /// <param name="ct">Cancellation.</param>
    Task<MailResult> SendAsync(string templateKey, IEnumerable<string> to, IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>? lists = null, string? replyTo = null, CancellationToken ct = default);

    /// <summary>True when the site can send mail at all (an SMTP host and sender, or the cloud relay).</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);
}

// ---- files ------------------------------------------------------------------------------------------------------

/// <summary>
/// A private place for a plugin's files: product downloads, invoices — everything that must not be reachable by address. It lies outside
/// the web root (not under <c>/uploads</c>, not under <c>/plugin-assets</c>), is carried by the backup, and is only ever served by a
/// handler of the plugin that decides who may have it.
/// <para>A name is a short relative path of letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, at most four levels (<c>products/12/guide.pdf</c>);
/// anything else is refused, so a name that came from a visitor cannot leave the folder.</para>
/// </summary>
public interface IPluginFiles
{
    /// <summary>Writes (or replaces) a file. The stream is read to its end and not disposed.</summary>
    Task SaveAsync(string name, Stream content, CancellationToken ct = default);

    /// <summary>Opens a file for reading; null when it does not exist. The caller disposes the stream.</summary>
    Task<Stream?> OpenAsync(string name, CancellationToken ct = default);

    /// <summary>The size in bytes, or null when the file does not exist.</summary>
    Task<long?> LengthAsync(string name, CancellationToken ct = default);

    /// <summary>Deletes a file. False when there was none.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken ct = default);

    /// <summary>True when <paramref name="name"/> is an acceptable file name for this store.</summary>
    bool IsValidName(string name);
}
