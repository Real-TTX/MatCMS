using MatCMS.Cloud.Services;
using MatCMS.Shared;
using MatCMS.Shared.Ai;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.Settings;

/// <summary>
/// One AI profile of the cloud (Einstellungen → KI → profile): create or edit. "Modelle laden" and "Testen" use the values in the
/// form, falling back to the stored key when the key field is empty (it is never sent to the page). The list, activating and
/// deleting are on the KI tab.
/// </summary>
public class AiProfileModel : PageModel
{
    private readonly AiProfileService _profiles;
    private readonly ChatGptAuthService _chatgpt;
    private readonly IHttpClientFactory _http;

    public AiProfileModel(AiProfileService profiles, ChatGptAuthService chatgpt, IHttpClientFactory http)
    {
        _profiles = profiles; _chatgpt = chatgpt; _http = http;
    }

    public AiProfileService.Profile Profile { get; private set; } = new();
    public bool IsNew { get; private set; }
    public bool IsActive { get; private set; }
    public bool HasKey => Profile.Key.Length > 0;
    public bool ChatGptConnected { get; private set; }
    public IReadOnlyList<ChatGptAuthService.ModelInfo> ChatGptModels { get; private set; } = Array.Empty<ChatGptAuthService.ModelInfo>();

    /// <summary>Starting points for the address — the providers people actually use, plus "own server".</summary>
    public static readonly (string Id, string Label, string Url, string ModelHint)[] Presets =
    [
        ("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini"),
        ("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-chat"),
        ("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "deepseek/deepseek-chat-v3-0324:free"),
        ("custom", "", "", ""),
    ];

    public async Task<IActionResult> OnGetAsync(string? id)
    {
        var set = _profiles.Get();
        if (id is not null)
        {
            if (set.Profiles.FirstOrDefault(p => p.Id == id) is not { } found) return NotFound();
            Profile = found;
            IsActive = found.Id == set.ActiveId;
        }
        else IsNew = true;
        ChatGptConnected = await _chatgpt.IsConnectedAsync();
        if (ChatGptConnected) ChatGptModels = await _chatgpt.ListModelsAsync(HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? id, string? name, string? kind, string? baseUrl, string? model, string? chatgptModel,
        string? apiKey, bool clearKey, bool activate)
    {
        if (id is not null && _profiles.Get().Profiles.All(p => p.Id != id)) return NotFound();
        var chat = kind == "chatgpt";
        var p = await _profiles.SaveAsync(new AiProfileService.Profile
        {
            Id = id ?? "", Name = name ?? "", Kind = chat ? "chatgpt" : "openai", BaseUrl = baseUrl ?? "", Model = (chat ? chatgptModel : model) ?? "",
        }, apiKey, clearKey, activate);
        TempData["Flash"] = activate ? $"KI-Profil „{p.Name}“ gespeichert und aktiviert." : $"KI-Profil „{p.Name}“ gespeichert.";
        return RedirectToPage("Index", new { tab = "ai" });
    }

    public async Task<IActionResult> OnPostModelsAsync(string? id, string? baseUrl, string? apiKey)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _profiles.KeyOf(id) : apiKey.Trim();
        var (models, error) = await ChatCompletionsClient.ListModelsAsync(_http.CreateClient(), baseUrl ?? "", key, HttpContext.RequestAborted);
        return new JsonResult(new { ok = models is not null, error, models = models?.Select(m => new { id = m.Id, name = m.Name, free = m.Free }) });
    }

    public async Task<IActionResult> OnPostTestAsync(string? id, string? kind, string? baseUrl, string? model, string? chatgptModel, string? apiKey)
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
        if (kind == "chatgpt")
            r = string.IsNullOrWhiteSpace(chatgptModel)
                ? new AiResponse { Ok = false, Error = "Kein ChatGPT-Modell gewählt." }
                : await _chatgpt.CompleteAsync(chatgptModel.Trim(), req.Messages, HttpContext.RequestAborted);
        else
        {
            var key = string.IsNullOrWhiteSpace(apiKey) ? _profiles.KeyOf(id) : apiKey.Trim();
            r = key.Length == 0 || string.IsNullOrWhiteSpace(model)
                ? new AiResponse { Ok = false, Error = "API-Schlüssel oder Modell fehlt." }
                : await ChatCompletionsClient.CompleteAsync(_http.CreateClient(), baseUrl ?? "", key, model.Trim(), req, 16000, null, HttpContext.RequestAborted);
        }
        return new JsonResult(new { ok = r.Ok, text = r.Text, error = r.Error, model = r.Model });
    }
}
