// Translation-compare inline editor. Clicking a block cell opens a dialog that shows the ORIGINAL
// block (read-only, left) next to the TARGET-language block with its editable fields (right), so a
// translation can be fixed on the spot. Both sides are built from the real block-editor schema
// (Diff?handler=Block, the builder the page editor uses) via window.MatBlockFields.build — same
// controls as the editor; a save goes to Diff?handler=SaveBlock.
(function () {
    "use strict";

    var table = document.querySelector(".d-table[data-src-page]");
    if (!table || !window.MatBlockFields) return;
    var srcPage = parseInt(table.getAttribute("data-src-page"), 10) || 0;

    var L = (window.matDiffI18n || {});
    function t(k, d) { return L[k] || d; }

    table.addEventListener("click", function (e) {
        var cell = e.target.closest(".d-cell");
        if (!cell) return;
        var row = cell.closest("tr");
        var srcBlock = parseInt(row.getAttribute("data-src-block"), 10) || 0;
        var tgtPage = parseInt(cell.getAttribute("data-tgt-page"), 10) || 0;
        var tgtBlock = parseInt(cell.getAttribute("data-tgt-block"), 10) || 0;
        if (!srcBlock && !tgtBlock) return; // nothing to show
        openDialog({
            loc: cell.getAttribute("data-loc") || "",
            blockType: row.getAttribute("data-block-type") || "",
            srcBlock: srcBlock, tgtPage: tgtPage, tgtBlock: tgtBlock
        });
    });

    // This page's own address (/admin/pages/diff/{id}) carries the handlers; the antiforgery token is the layout's.
    var self = location.pathname;
    var tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
    var token = tokEl ? tokEl.value : "";

    async function loadBlock(pageId, blockId) {
        var res = await fetch(self + "?handler=Block&blockId=" + blockId, { credentials: "same-origin" });
        if (!res.ok) throw new Error("load " + res.status);
        var json = await res.json();
        return { schema: json.schema || [], data: json.data || {}, token: token };
    }

    async function save(pageId, blockId, dataObj, token) {
        var body = new URLSearchParams();
        body.set("DataJson", JSON.stringify(dataObj));
        if (token) body.set("__RequestVerificationToken", token);
        var res = await fetch(self + "?handler=SaveBlock&blockId=" + blockId, {
            method: "POST", credentials: "same-origin",
            headers: { "Content-Type": "application/x-www-form-urlencoded" },
            body: body.toString()
        });
        return res.ok;
    }

    function el(tag, cls, txt) {
        var n = document.createElement(tag);
        if (cls) n.className = cls;
        if (txt != null) n.textContent = txt;
        return n;
    }

    function openDialog(ctx) {
        var dlg = document.createElement("dialog");
        dlg.className = "de-dialog";

        var head = el("div", "de-head");
        head.appendChild(el("strong", null, t("editTitle", "Übersetzung bearbeiten") + " · " + ctx.loc.toUpperCase() + " · " + ctx.blockType));
        var x = el("button", "de-x", ""); x.type = "button"; x.innerHTML = '<i class="ti ti-x" aria-hidden="true"></i>';
        x.addEventListener("click", function () { dlg.close(); });
        head.appendChild(x);
        dlg.appendChild(head);

        var grid = el("div", "de-grid");
        var left = el("div", "de-col");
        left.appendChild(el("div", "de-coltitle", t("original", "Original") + " (" + (window.matDiffDefault || "") + ")"));
        var leftBody = el("div", "de-fields de-readonly"); left.appendChild(leftBody);
        var right = el("div", "de-col");
        right.appendChild(el("div", "de-coltitle", t("translation", "Übersetzung") + " (" + ctx.loc.toUpperCase() + ")"));
        var rightBody = el("div", "de-fields"); right.appendChild(rightBody);
        grid.appendChild(left); grid.appendChild(right);
        dlg.appendChild(grid);

        var foot = el("div", "de-foot");
        var msg = el("span", "de-msg");
        var cancel = el("button", "btn btn-ghost btn-sm", t("cancel", "Abbrechen")); cancel.type = "button";
        cancel.addEventListener("click", function () { dlg.close(); });
        var saveBtn = el("button", "btn btn-sm", t("save", "Speichern")); saveBtn.type = "button";
        foot.appendChild(msg); foot.appendChild(cancel); foot.appendChild(saveBtn);
        dlg.appendChild(foot);

        document.body.appendChild(dlg);
        dlg.addEventListener("close", function () { dlg.remove(); });
        dlg.showModal();

        // Left: original block, read-only.
        if (ctx.srcBlock) {
            loadBlock(srcPage, ctx.srcBlock).then(function (b) {
                window.MatBlockFields.build(leftBody, b.schema, b.data);
                leftBody.querySelectorAll("input,textarea,select,button").forEach(function (i) { i.disabled = true; });
                leftBody.querySelectorAll('[contenteditable]').forEach(function (i) { i.setAttribute("contenteditable", "false"); });
            }).catch(function () { leftBody.appendChild(el("p", "muted", "—")); });
        } else {
            leftBody.appendChild(el("p", "muted", t("noOriginal", "Kein Original-Block (nur in dieser Sprache vorhanden).")));
        }

        // Right: target block, editable + save.
        if (ctx.tgtBlock) {
            loadBlock(ctx.tgtPage, ctx.tgtBlock).then(function (b) {
                var api = window.MatBlockFields.build(rightBody, b.schema, b.data);
                saveBtn.addEventListener("click", function () {
                    saveBtn.disabled = true; msg.textContent = t("saving", "Speichern…");
                    save(ctx.tgtPage, ctx.tgtBlock, api.serialize(), b.token).then(function (ok) {
                        if (ok) { msg.textContent = t("saved", "Gespeichert."); location.reload(); }
                        else { saveBtn.disabled = false; msg.textContent = t("saveError", "Fehler beim Speichern."); }
                    });
                });
            }).catch(function () { rightBody.appendChild(el("p", "muted", "—")); });
        } else {
            rightBody.appendChild(el("p", "muted", t("noTarget", "In dieser Sprache existiert dieser Block (noch) nicht.")));
            saveBtn.disabled = true;
        }
    }
})();
