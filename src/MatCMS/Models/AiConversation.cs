namespace MatCMS.Models;

/// <summary>
/// One conversation with the AI assistant (Admin → KI-Assistent). A conversation is a series of RUNS: every message the
/// editor sends starts one, and the assistant works until it is done or waiting for an answer. What happened is kept as
/// <see cref="AiStep"/> rows (what the editor reads), what the model has been told as <see cref="HistoryJson"/> (what the
/// next call sends), and what a run changed as <see cref="AiChange"/> snapshots (what "Rückgängig" restores).
/// <para>Not part of a backup: a conversation is a log of work on THIS site, and its snapshots name row ids that mean
/// nothing on another one.</para>
/// </summary>
public class AiConversation
{
    public int Id { get; set; }

    /// <summary>The first words of the first message, for the list.</summary>
    public string Title { get; set; } = "";

    /// <summary>Who started it (username).</summary>
    public string CreatedBy { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>"idle" | "running".</summary>
    public string Status { get; set; } = "idle";

    /// <summary>The page the conversation is about (opened from the page editor), or null for the whole site.</summary>
    public int? ScopePageId { get; set; }

    /// <summary>The block the conversation is about (opened from a block), or null.</summary>
    public int? ScopeBlockId { get; set; }

    /// <summary>Changes are limited to the scope (the page, or the block and what is inside it). Off = the scope is only
    /// the starting point and the assistant may work anywhere.</summary>
    public bool ScopeLocked { get; set; }

    /// <summary>The model-facing messages so far (user / assistant / tool results), as a JSON array of {role, content}.
    /// The system prompt is NOT stored: it is built fresh for every call, so a changed instruction applies at once.</summary>
    public string HistoryJson { get; set; } = "[]";

    /// <summary>How many runs (messages from the editor) the conversation has had; numbers <see cref="AiStep.Run"/>.</summary>
    public int Runs { get; set; }

    /// <summary>Tokens the provider reported for this conversation, summed.</summary>
    public int Tokens { get; set; }
}

/// <summary>One visible step of a conversation, in order. The editor sees exactly these.</summary>
public class AiStep
{
    public int Id { get; set; }
    public int ConversationId { get; set; }

    /// <summary>Which run of the conversation this belongs to (1, 2, …).</summary>
    public int Run { get; set; }

    /// <summary>"user" | "assistant" | "action" | "error" | "info".</summary>
    public string Kind { get; set; } = "";

    /// <summary>The text: the editor's message, the assistant's message, an action's one-line summary, an error.</summary>
    public string Text { get; set; } = "";

    /// <summary>Details as JSON: for "assistant" the raw reply, model and tokens; for "action" the tool, its arguments, the
    /// result and warnings; for "user" the attachments.</summary>
    public string? DataJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// What a run found before it changed something — the material for "Rückgängig". Written the FIRST time a run touches an
/// entity, so the snapshot is the state before the run, however often the run changed it afterwards.
/// </summary>
public class AiChange
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public int Run { get; set; }

    /// <summary>"page" | "menu" | "template" | "setting" | "media".</summary>
    public string Kind { get; set; } = "";

    /// <summary>Which one: a page id, "header|de" for a menu, a template id, a setting key, a media id.</summary>
    public string Key { get; set; } = "";

    /// <summary>The state before the run as JSON; null when the run CREATED it (undo deletes it).</summary>
    public string? BeforeJson { get; set; }

    /// <summary>Set once the run was undone.</summary>
    public bool Reverted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
