/* The back office's own confirm / alert / prompt — never the browser's window.confirm/alert/prompt, which look
   foreign, ignore the colour scheme, cannot be styled as "dangerous" and block the whole tab.

   MatDialog.confirm(message, { ok, cancel, danger }) → Promise<boolean>
   MatDialog.alert(message, { ok })                   → Promise<void>
   MatDialog.prompt(message, value, { ok, cancel })   → Promise<string|null>   (null = cancelled)

   Declarative: a <form data-confirm="…"> (or a submit button / link with data-confirm) asks first and then
   submits with the same button, so a handler that depends on which button was pressed still sees it.
   Add data-confirm-danger to colour the OK button red; a form whose submit button is .btn-danger gets that
   automatically.

   The button wording comes from <html data-dlg-ok / data-dlg-cancel> (set by each app's layout, which owns the
   translations); a shared library cannot reach either app's localizer. Loaded in <head>: it only defines
   things, and inline page scripts may already reference MatDialog. */
(function () {
    "use strict";
    if (window.MatDialog) return;

    function label(name, fallback) {
        var v = document.documentElement.getAttribute("data-dlg-" + name);
        return v || fallback;
    }

    function open(kind, message, value, opts) {
        opts = opts || {};
        return new Promise(function (resolve) {
            var dlg = document.createElement("dialog");
            dlg.className = "mat-dialog mat-dlg";
            dlg.setAttribute("aria-modal", "true");
            var msg = document.createElement("p");
            msg.className = "mat-dlg-msg";
            msg.textContent = message == null ? "" : String(message);
            dlg.appendChild(msg);

            var input = null;
            if (kind === "prompt") {
                input = document.createElement("input");
                input.type = "text";
                input.className = "mat-dlg-input";
                input.value = value == null ? "" : String(value);
                input.setAttribute("autocomplete", "off");
                dlg.appendChild(input);
            }

            var actions = document.createElement("div");
            actions.className = "form-actions mat-dlg-actions";
            var cancel = null;
            if (kind !== "alert") {
                cancel = document.createElement("button");
                cancel.type = "button";
                cancel.className = "btn btn-ghost";
                cancel.textContent = opts.cancel || label("cancel", "Abbrechen");
                actions.appendChild(cancel);
            }
            var ok = document.createElement("button");
            ok.type = "button";
            ok.className = "btn" + (opts.danger ? " btn-danger" : "");
            ok.textContent = opts.ok || label("ok", "OK");
            actions.appendChild(ok);
            dlg.appendChild(actions);

            var done = false;
            function finish(result) {
                if (done) return;
                done = true;
                if (dlg.open) dlg.close();
                dlg.remove();
                resolve(result);
            }
            ok.addEventListener("click", function () {
                finish(kind === "prompt" ? input.value : kind === "confirm" ? true : undefined);
            });
            if (cancel) cancel.addEventListener("click", function () { finish(kind === "prompt" ? null : false); });
            // Escape / the dialog's own cancel = "Abbrechen" (for an alert simply "close").
            dlg.addEventListener("cancel", function (e) { e.preventDefault(); finish(kind === "prompt" ? null : kind === "confirm" ? false : undefined); });
            if (input) input.addEventListener("keydown", function (e) { if (e.key === "Enter") { e.preventDefault(); ok.click(); } });

            document.body.appendChild(dlg);
            dlg.showModal();
            if (input) { input.focus(); input.select(); }
            // A destructive confirm starts on "Abbrechen", so an Enter out of habit does not delete.
            else if (cancel && opts.danger) cancel.focus();
            else ok.focus();
        });
    }

    window.MatDialog = {
        confirm: function (message, opts) { return open("confirm", message, null, opts); },
        alert: function (message, opts) { return open("alert", message, null, opts); },
        prompt: function (message, value, opts) { return open("prompt", message, value, opts); }
    };

    // --- declarative data-confirm ------------------------------------------------------------------
    // Capture phase, so this runs before any other submit handler of the page; a confirmed form is
    // re-submitted with requestSubmit(submitter) and passes through once (the "confirmed" flag).
    function isDanger(el, form) {
        if (el && el.hasAttribute && el.hasAttribute("data-confirm-danger")) return true;
        if (form && form.hasAttribute("data-confirm-danger")) return true;
        var btn = form && form.querySelector("button[type=submit].btn-danger, button:not([type]).btn-danger");
        return !!btn;
    }
    document.addEventListener("submit", function (e) {
        var form = e.target;
        if (!(form instanceof HTMLFormElement)) return;
        var submitter = e.submitter || null;
        var text = (submitter && submitter.getAttribute("data-confirm")) || form.getAttribute("data-confirm");
        if (!text) return;
        if (form.__matConfirmed) { form.__matConfirmed = false; return; }
        e.preventDefault();
        e.stopImmediatePropagation();
        MatDialog.confirm(text, { danger: isDanger(submitter, form) }).then(function (yes) {
            if (!yes) return;
            form.__matConfirmed = true;
            if (form.requestSubmit) form.requestSubmit(submitter && submitter.form === form ? submitter : undefined);
            else form.submit();
        });
    }, true);
    // Links and plain buttons with data-confirm (outside a form submit).
    document.addEventListener("click", function (e) {
        var el = e.target.closest && e.target.closest("a[data-confirm], button[data-confirm]:not([type=submit])");
        if (!el || el.__matConfirmed) { if (el) el.__matConfirmed = false; return; }
        if (el.tagName === "BUTTON" && el.form && (el.type === "submit")) return;   // handled by the submit listener
        e.preventDefault();
        e.stopImmediatePropagation();
        MatDialog.confirm(el.getAttribute("data-confirm"), { danger: el.hasAttribute("data-confirm-danger") || el.classList.contains("btn-danger") })
            .then(function (yes) {
                if (!yes) return;
                el.__matConfirmed = true;
                el.click();
            });
    }, true);
})();
