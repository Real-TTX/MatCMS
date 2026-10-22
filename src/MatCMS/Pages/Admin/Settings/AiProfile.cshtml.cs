using MatCMS.Services;
using MatCMS.Shared;
using MatCMS.Shared.Ai;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Settings;

/// <summary>
/// One AI profile (Einstellungen → KI → profile): create or edit. The list, activating and deleting live on the KI tab; this
/// page asks for the connection itself and can try it — "Modelle laden" and "Testen" use the values in the form, falling back
/// to the stored key when the key field is left empty (it is never sent to the page).
/// </summary>
public class AiProfileModel : PageModel
{
    private readonly OwnAi _own;
    private readonly SiteChatGptAccounts _chatgpt;
    private readonly IHttpClientFactory _http;

    public AiProfileModel(OwnAi own, SiteChatGptAccounts chatgpt, IHttpClientFactory http)
    {
        _own = own; _chatgpt = chatgpt; _http = http;
    }

    public OwnAi.Profile Profile { get; private set; } = new();
    public bool IsNew { get; private set; }
    public bool IsActive { get; private set; }
    public bool HasKey => Profile.Key.Length > 0;

    /// <summary>Starting points for the address: the providers people actually use, plus "own server".</summary>
    public static readonly (string Id, string Label, string Url, string ModelHint)[] Presets =
    [
        ("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini"),
        ("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-chat"),
        ("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "deepseek/deepseek-chat-v3-0324:free"),
        ("custom", "", "", ""),
    ];

    public async Task<IActionResult> OnGetAsync(string? id)
    {
        var set = await _own.ProfilesAsync(HttpContext.RequestAborted);
        if (id is not null)
        {
            if (set.Profiles.FirstOrDefault(p => p.Id == id) is not { } found) return NotFound();
            Profile = found;
            IsActive = found.Id == set.ActiveId;
        }
        else
        {
            IsNew = true;
            Profile = new OwnAi.Profile { Kind = "openai" };
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? id, string? name, string? provider, string? baseUrl, string? model, string? apiKey,
        bool clearKey, string? chatgptModel, bool activate)
    {
        var set = await _own.ProfilesAsync(HttpContext.RequestAborted);
        var p = id is null ? null : set.Profiles.FirstOrDefault(x => x.Id == id);
        if (id is not null && p is null) return NotFound();
        if (p is null)
        {
            p = new OwnAi.Profile { Id = Guid.NewGuid().ToString("N")[..10] };
            set.Profiles.Add(p);
        }
        p.Kind = provider == "chatgpt" ? "chatgpt" : "openai";
        p.BaseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        p.Model = (model ?? "").Trim();
        p.ChatGptModel = (chatgptModel ?? "").Trim();
        if (clearKey) p.Key = "";
        else if (!string.IsNullOrWhiteSpace(apiKey)) p.Key = _own.Protect(apiKey.Trim());
        var n = (name ?? "").Trim();
        p.Name = n.Length > 0 ? (n.Length > 80 ? n[..80] : n) : OwnAi.DefaultName(p.Kind, p.BaseUrl, p.ModelShown);
        await _own.SaveProfilesAsync(activate ? set with { ActiveId = p.Id } : set, HttpContext.RequestAborted);
        TempData["Flash"] = activate ? $"KI-Profil „{p.Name}“ gespeichert und aktiviert." : $"KI-Profil „{p.Name}“ gespeichert.";
        // A ChatGPT profile that is not signed in yet comes back here: the sign-in is the next step, and it lives on this page.
        if (p.Kind == "chatgpt" && !await _own.HasSignInAsync(p.Id, HttpContext.RequestAborted)) return RedirectToPage(new { id = p.Id });
        return RedirectToPage("Index", new { tab = "ai" });
    }

    /// <summary>The models the provider offers, for the model field — with what the form says, or the stored key.</summary>
    public async Task<IActionResult> OnPostModelsAsync(string? id, string? baseUrl, string? apiKey)
    {
        var key = await KeyAsync(id, apiKey);
        var (models, error) = await ChatCompletionsClient.ListModelsAsync(_http.CreateClient(), baseUrl ?? "", key, HttpContext.RequestAborted);
        return new JsonResult(new { ok = models is not null, error, models = models?.Select(m => new { id = m.Id, name = m.Name, free = m.Free }) });
    }

    /// <summary>A short call with the values in the form, before (or without) saving them.</summary>
    public async Task<IActionResult> OnPostTestAsync(string? id, string? provider, string? baseUrl, string? model, string? apiKey, string? chatgptModel)
    {
        var req = new AiRequest
        {
            Purpose = "test", MaxTokens = 1500,
            Messages =
            {
                new AiMessage { Role = "system", Content = "Antworte in einem kurzen deutschen Satz." },
                new AiMessage { Role = "user", Content = "Sag Hallo und nenne den Namen des Modells, das gerade antwortet." },
            },
        };
        AiResponse r;
        if (provider == "chatgpt")
            r = string.IsNullOrWhiteSpace(chatgptModel)
                ? new AiResponse { Ok = false, Error = "Kein ChatGPT-Modell gewählt." }
                : id is null ? new AiResponse { Ok = false, Error = "Erst speichern und anmelden." }
                : await _chatgpt.For(id).CompleteAsync(chatgptModel.Trim(), req.Messages, HttpContext.RequestAborted);
        else
        {
            var key = await KeyAsync(id, apiKey);
            r = key.Length == 0 || string.IsNullOrWhiteSpace(model)
                ? new AiResponse { Ok = false, Error = "API-Schlüssel oder Modell fehlt." }
                : await ChatCompletionsClient.CompleteAsync(_http.CreateClient(), baseUrl ?? "", key, model.Trim(), req, SiteAiTransport.MaxCompletionTokens,
                    null, HttpContext.RequestAborted);
        }
        return new JsonResult(new { ok = r.Ok, text = r.Text, error = r.Error, model = r.Model, note = r.Note });
    }

    // ---- the ChatGPT sign-in of THIS profile (device code): started, polled and ended from the profile page ----

    private async Task<string?> ExistingAsync(string? id) =>
        id is not null && (await _own.ProfilesAsync(HttpContext.RequestAborted)).Profiles.Any(p => p.Id == id) ? id : null;

    public async Task<IActionResult> OnPostChatGptLoginAsync(string? id)
    {
        if (await ExistingAsync(id) is not { } pid) return new JsonResult(new { ok = false, error = "Erst das Profil speichern." });
        var account = _chatgpt.For(pid);
        var (_, error) = await account.StartLoginAsync(HttpContext.RequestAborted);
        var status = await account.GetStatusAsync(poll: false, HttpContext.RequestAborted);
        return new JsonResult(new { ok = error is null, error, status = MatCMS.Shared.Ai.ChatGptAccount.StatusJson(status) });
    }

    /// <summary>Polled while a login runs: advances it at most one step per OpenAI interval, then lists the account's models.</summary>
    public async Task<IActionResult> OnGetChatGptStatusAsync(string? id)
    {
        if (await ExistingAsync(id) is not { } pid) return new JsonResult(new { connected = false });
        var account = _chatgpt.For(pid);
        var status = await account.GetStatusAsync(poll: true, HttpContext.RequestAborted);
        var models = status.Connected ? await account.ListModelsAsync(HttpContext.RequestAborted) : null;
        return new JsonResult(MatCMS.Shared.Ai.ChatGptAccount.StatusJson(status, models));
    }

    public async Task<IActionResult> OnPostChatGptLogoutAsync(string? id)
    {
        if (await ExistingAsync(id) is { } pid) await _chatgpt.For(pid).LogoutAsync(HttpContext.RequestAborted);
        return new JsonResult(new { ok = true });
    }

    private async Task<string> KeyAsync(string? id, string? typed)
    {
        if (!string.IsNullOrWhiteSpace(typed)) return typed.Trim();
        if (id is null) return "";
        var set = await _own.ProfilesAsync(HttpContext.RequestAborted);
        return set.Profiles.FirstOrDefault(p => p.Id == id) is { } p ? _own.Unprotect(p.Key) : "";
    }
}
