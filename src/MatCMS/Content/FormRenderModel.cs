namespace MatCMS.Content;

/// <summary>View model handed to the shared <c>Blocks/_FormRender</c> partial.</summary>
public class FormRenderModel
{
    public int FormId { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Heading { get; set; }
    public string? Intro { get; set; }

    /// <summary>Custom submit-button label (empty = localized default).</summary>
    public string? SubmitLabel { get; set; }
    public List<FormElement> Elements { get; set; } = new();

    /// <summary>Preview mode: render a non-submitting form (used in the builder iframe).</summary>
    public bool Preview { get; set; }

    /// <summary>When true, the preview also emits the select-on-click builder bridge script.</summary>
    public bool Builder { get; set; }

    public string? Success { get; set; }
    public List<string> Errors { get; set; } = new();
    public Dictionary<string, string> Values { get; set; } = new();

    // --- Anti-spam (see Services.FormGuard) ---------------------------------------------------------
    /// <summary>Effective protection level (0 off … 3 captcha). 0 renders no guard markup.</summary>
    public int SpamLevel { get; set; }
    /// <summary>DataProtection-signed token that carries the checks' secrets (hidden field).</summary>
    public string? GuardToken { get; set; }
    /// <summary>Nonce the client script writes back on first interaction to prove JS ran.</summary>
    public string? GuardNonce { get; set; }
    /// <summary>Proof-of-work difficulty in leading zero bits (0 = no PoW, level &lt; 2).</summary>
    public int PowBits { get; set; }
    /// <summary>Proof-of-work challenge the client hashes against (level ≥ 2).</summary>
    public string? Challenge { get; set; }
    /// <summary>Captcha operands (level ≥ 3): the visitor answers <c>CaptchaA + CaptchaB</c>.</summary>
    public int CaptchaA { get; set; }
    public int CaptchaB { get; set; }
}
