// MatPlatform 2.4.0 – a copy from MatDesign, do not edit here
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MatPlatform;

/// <summary>A person of the app as MatIdentity's SCIM endpoint answers them (<see cref="MatPeople"/>): who they are, and their role, permissions and quotas in this app.</summary>
public sealed record MatPerson(string Subject, string UserName, string? DisplayName, string? Email, bool Active)
{
    /// <summary>The role in this app; null when MatIdentity does not decide the roles of this app.</summary>
    public string? Role { get; init; }

    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>The quotas MatIdentity set (null: no limit); a key that is not there is the app's own setting.</summary>
    public IReadOnlyDictionary<string, long?> Quotas { get; init; } = new Dictionary<string, long?>();

    /// <summary>The names of the person's groups.</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>The name to show: the display name, else the user name.</summary>
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? UserName : DisplayName;
}

/// <summary>A group as MatIdentity's SCIM endpoint answers it: its id, its name and the subjects of its members who may use this app.</summary>
public sealed record MatPeopleGroup(string Id, string Name, IReadOnlyList<string> Members);

/// <summary>
/// 2.4.0: the people of the app before they sign in (docs/PLATFORM.md "People, permissions and quotas", capability <c>people.scim</c>): reads MatIdentity's SCIM
/// endpoint with the key of the binding. MatIdentity answers only the people who may use this app, with their role, permissions and quotas here. An app that
/// needs it consumes <c>people.scim</c> (<c>o.Consumes("people.scim", optional: true, ...)</c>); without the binding every answer is empty.
/// </summary>
public sealed class MatPeople
{
    public const string Capability = "people.scim";

    /// <summary>The name of the <see cref="HttpClient"/> (tests replace its handler).</summary>
    public const string HttpClientName = "MatPlatform.People";

    public const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";

    public const string AccessSchema = "urn:mat:scim:schemas:extension:access:1.0:User";

    private const int PageSize = 500;

    private readonly MatBindings _bindings;
    private readonly IHttpClientFactory _http;
    private readonly Lazy<string> _appId;

    public MatPeople(MatBindings bindings, IHttpClientFactory http, MatPlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _bindings = bindings;
        _http = http;
        _appId = new Lazy<string>(() => options.Definition(options.DefaultLanguage, new MatAuthInfo(false, null)).App.Id);
    }

    /// <summary>The binding <c>people.scim</c> is there (by the variables or by hand).</summary>
    public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
        => (await _bindings.GetAsync(Capability, ct)) is { IsBound: true, ServerUrl: not null, Key: not null };

    /// <summary>The person with the subject <paramref name="subject"/>; null when there is none who may use the app, or no binding.</summary>
    public async Task<MatPerson?> FindAsync(string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        using var document = await GetAsync("Users/" + Uri.EscapeDataString(subject), ct);
        return document is null ? null : Person(document.RootElement);
    }

    /// <summary>The person with the user name <paramref name="userName"/>.</summary>
    public async Task<MatPerson?> FindByUserNameAsync(string userName, CancellationToken ct = default)
        => (await QueryAsync("Users", Filter("userName", userName), ct)).Select(Person).OfType<MatPerson>().FirstOrDefault();

    /// <summary>The person with the e-mail address <paramref name="email"/>.</summary>
    public async Task<MatPerson?> FindByEmailAsync(string email, CancellationToken ct = default)
        => (await QueryAsync("Users", Filter("emails.value", email), ct)).Select(Person).OfType<MatPerson>().FirstOrDefault();

    /// <summary>Every person who may use the app (all pages).</summary>
    public async Task<IReadOnlyList<MatPerson>> ListAsync(CancellationToken ct = default)
        => [.. (await QueryAsync("Users", null, ct)).Select(Person).OfType<MatPerson>()];

    /// <summary>The groups, with those of their members who may use the app.</summary>
    public async Task<IReadOnlyList<MatPeopleGroup>> GroupsAsync(CancellationToken ct = default)
        => [.. (await QueryAsync("Groups", null, ct)).Select(Group).OfType<MatPeopleGroup>()];

    /// <summary>A SCIM filter <c>attribute eq "value"</c>, the value escaped.</summary>
    public static string Filter(string attribute, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        ArgumentNullException.ThrowIfNull(value);
        return attribute + " eq \"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>A person of a SCIM user resource, with the entry of this app in the extension <see cref="AccessSchema"/>; null without id or user name.</summary>
    public MatPerson? Person(JsonElement user) => PersonOf(user, _appId.Value);

    /// <summary><see cref="Person(JsonElement)"/> for the app <paramref name="appId"/> (for tests and other readers).</summary>
    public static MatPerson? PersonOf(JsonElement user, string appId)
    {
        if (user.ValueKind != JsonValueKind.Object || Text(user, "id") is not { } id || Text(user, "userName") is not { } userName)
        {
            return null;
        }

        string? email = null;
        if (user.TryGetProperty("emails", out var emails) && emails.ValueKind == JsonValueKind.Array)
        {
            var all = emails.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
            var chosen = all.FirstOrDefault(e => e.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True);
            email = Text(chosen.ValueKind == JsonValueKind.Object ? chosen : all.FirstOrDefault(), "value");
        }

        var groups = user.TryGetProperty("groups", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(g => Text(g, "display") ?? Text(g, "value")).OfType<string>().ToList()
            : [];

        var person = new MatPerson(id, userName, Text(user, "displayName"), email, !user.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.False)
        {
            Groups = groups,
        };

        if (!user.TryGetProperty(AccessSchema, out var extension) || !extension.TryGetProperty("access", out var access) || access.ValueKind != JsonValueKind.Array)
        {
            return person;
        }

        var entries = access.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        var mine = entries.FirstOrDefault(e => Text(e, "app") == appId);
        if (mine.ValueKind != JsonValueKind.Object)
        {
            return person;
        }

        var permissions = mine.TryGetProperty("permissions", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(MatAccessClaims.IsKey).ToList()
            : [];
        var quotas = new Dictionary<string, long?>(StringComparer.Ordinal);
        if (mine.TryGetProperty("quotas", out var q) && q.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in q.EnumerateObject().Where(e => MatAccessClaims.IsKey(e.Name)))
            {
                if (entry.Value.ValueKind == JsonValueKind.Null)
                {
                    quotas[entry.Name] = null;
                }
                else if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetInt64(out var number) && number >= 0)
                {
                    quotas[entry.Name] = number;
                }
            }
        }

        return person with { Role = Text(mine, "role"), Permissions = permissions, Quotas = quotas };
    }

    private static MatPeopleGroup? Group(JsonElement group)
    {
        if (group.ValueKind != JsonValueKind.Object || Text(group, "id") is not { } id)
        {
            return null;
        }

        var members = group.TryGetProperty("members", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(m => Text(m, "value")).OfType<string>().ToList()
            : [];
        return new MatPeopleGroup(id, Text(group, "displayName") ?? id, members);
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>Every resource of a list (all pages); empty without a binding.</summary>
    private async Task<IReadOnlyList<JsonElement>> QueryAsync(string resource, string? filter, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        var start = 1;
        while (true)
        {
            var query = $"{resource}?startIndex={start}&count={PageSize}" + (filter is null ? string.Empty : "&filter=" + Uri.EscapeDataString(filter));
            using var page = await GetAsync(query, ct);
            if (page is null || !page.RootElement.TryGetProperty("Resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
            {
                return all;
            }

            var count = 0;
            foreach (var item in resources.EnumerateArray())
            {
                all.Add(item.Clone());
                count++;
            }

            var total = page.RootElement.TryGetProperty("totalResults", out var t) && t.TryGetInt32(out var n) ? n : all.Count;
            if (count == 0 || all.Count >= total)
            {
                return all;
            }

            start += count;
        }
    }

    /// <summary>A GET of the SCIM endpoint; null without a binding or for a 404. Any other failure throws (<see cref="HttpRequestException"/>).</summary>
    private async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        var binding = await _bindings.GetAsync(Capability, ct);
        if (binding is not { IsBound: true, ServerUrl: { } server, Key: { } key })
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, server.TrimEnd('/') + "/scim/v2/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/scim+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.CreateClient(HttpClientName).SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"MatIdentity answered {(int)response.StatusCode} to SCIM {path}: {(body.Length > 300 ? body[..300] : body)}", null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}
