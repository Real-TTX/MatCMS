namespace MatCMS.Shared.Ai;

/// <summary>
/// Starting points for an AI profile's address — the providers people actually use, plus "own server" (an empty URL). One list for the CMS's and the
/// cloud's profile page; both speak the same OpenAI-compatible chat API through <see cref="ChatCompletionsClient"/>.
/// <para>Mistral is the one inside the EU (Mistral AI, Paris; data processed in the EU) — the choice for a site whose VISITORS write to the AI (the
/// AI bot), where a provider outside the EU means a transfer to a third country.</para>
/// </summary>
public static class AiPresets
{
    public static readonly (string Id, string Label, string Url, string ModelHint)[] All =
    [
        ("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini"),
        ("mistral", "Mistral (EU)", "https://api.mistral.ai/v1", "mistral-small-latest"),
        ("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-chat"),
        ("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "deepseek/deepseek-chat-v3-0324:free"),
        ("custom", "", "", ""),
    ];
}
