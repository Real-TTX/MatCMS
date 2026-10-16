namespace MatCMS.Plugin;

/// <summary>Reads a component tree: what is in it, without drawing it. Used by the host's checks and by anything that
/// wants to look at a plugin's screen (a preview, a test, a tool) rather than render it.</summary>
public static class UiTree
{
    /// <summary>The node and everything below it, depth first.</summary>
    public static IEnumerable<UiNode> Descendants(UiNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var stack = new Stack<UiNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in ChildrenOf(n).Reverse()) stack.Push(c);
        }
    }

    /// <summary>The direct children of a node (cells of a table row included).</summary>
    public static IEnumerable<UiNode> ChildrenOf(UiNode node) => node switch
    {
        UiStack s => s.Children,
        UiInline i => i.Children,
        UiGrid g => g.Children,
        UiCard c => c.Children,
        UiSplit sp => sp.Children,
        UiColumns cols => cols.Main.Concat(cols.Side),
        UiPageHead h => h.Actions,
        UiForm f => f.Children,
        UiFormActions a => a.Children,
        UiDialog d => d.Children,
        UiTable t => t.Rows.SelectMany(r => r.Cells).Concat(t.Below),
        _ => Array.Empty<UiNode>()
    };

    /// <summary>
    /// Notes on how a tree sidesteps the library: raw HTML that carries fixed colours or its own style block would not
    /// follow the dark mode or the site's theme. Warnings, not errors — <see cref="UiHtml"/> exists for what no component
    /// covers — and meant for tests and packaging, not for the request path.
    /// </summary>
    public static IReadOnlyList<string> Lint(UiNode root)
    {
        var notes = new List<string>();
        foreach (var n in Descendants(root))
        {
            if (n is not UiHtml h) continue;
            var html = h.Html ?? "";
            if (html.Contains("<style", StringComparison.OrdinalIgnoreCase))
                notes.Add("UiHtml carries its own <style> block; use components or the plugin's asset stylesheet.");
            if (System.Text.RegularExpressions.Regex.IsMatch(html, @"(#[0-9a-fA-F]{3,8}\b|rgba?\()"))
                notes.Add("UiHtml carries a fixed colour; it will not follow the dark mode. Use the admin's CSS variables.");
            if (System.Text.RegularExpressions.Regex.IsMatch(html, @"\b(?:window\.)?(?:alert|confirm|prompt)\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                notes.Add("UiHtml calls a browser dialog; use data-confirm or MatDialog.");
        }
        return notes;
    }
}
