// Admin → KI-Assistent: the conversation. Sends a message (with files), then polls the steps the runner writes and shows
// each as it arrives — the AI's messages, every action with what it changed (details and the raw answer on demand), errors.
// The page the work is on is shown live on the right and reloads after every change.
(function () {
    'use strict';
    var root = document.getElementById('ai');

    // Tabs (?tab= deep link, like every tabbed admin page).
    var tabs = document.querySelectorAll('.tab[data-tab]');
    var panels = document.querySelectorAll('.tab-panel[data-panel]');
    function showTab(name) {
        tabs.forEach(function (t) { t.classList.toggle('active', t.dataset.tab === name); });
        panels.forEach(function (p) { p.hidden = p.dataset.panel !== name; });
    }
    tabs.forEach(function (t) { t.addEventListener('click', function () { showTab(t.dataset.tab); }); });

    if (!root) return;
    var W = JSON.parse(root.dataset.words || '{}');
    var convId = root.dataset.id ? parseInt(root.dataset.id, 10) : null;
    var log = document.getElementById('ai-log');
    var form = document.getElementById('ai-compose');
    var input = document.getElementById('ai-input');
    var fileInput = document.getElementById('ai-file');
    var filesBox = document.getElementById('ai-files');
    var sendBtn = document.getElementById('ai-send');
    var stopBtn = document.getElementById('ai-stop');
    var lock = document.getElementById('ai-lock');
    var frame = document.getElementById('ai-frame');
    var urlBox = document.getElementById('ai-url');
    var openLink = document.getElementById('ai-open');
    var token = form.querySelector('input[name="__RequestVerificationToken"]').value;

    var lastStep = 0;
    var running = false;
    var pollTimer = null;
    var pending = [];          // files chosen for the next message
    var currentUrl = root.dataset.preview || '/';
    var working = null;        // the "KI arbeitet …" line
    var runBoxes = {};         // run → its container (for the undo button)

    // ------------------------------------------------------------------ helpers

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text != null) e.textContent = text;
        return e;
    }

    // A small, safe Markdown: the text is escaped FIRST, then **bold**, `code`, line starts with "- " and line breaks.
    function md(text) {
        var s = String(text || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
        s = s.replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>').replace(/`([^`\n]+)`/g, '<code>$1</code>');
        s = s.replace(/^\s*[-*] (.*)$/gm, '• $1');
        return s.replace(/\n/g, '<br>');
    }

    function pretty(v) {
        try { return JSON.stringify(v, null, 2); } catch (e) { return String(v); }
    }

    function details(label, content) {
        var d = el('details', 'ai-details');
        d.appendChild(el('summary', null, label));
        var pre = el('pre', 'ai-pre');
        pre.textContent = content;
        d.appendChild(pre);
        return d;
    }

    function scrollDown() { log.scrollTop = log.scrollHeight; }

    function setPreview(url, reload) {
        if (!url) return;
        if (url !== currentUrl || reload) {
            currentUrl = url;
            urlBox.textContent = url;
            openLink.href = url;
            frame.src = url + (url.indexOf('?') >= 0 ? '&' : '?') + '_ai=' + Date.now();
        }
    }

    function runBox(run) {
        if (!runBoxes[run]) {
            var box = el('div', 'ai-run');
            box.dataset.run = run;
            log.appendChild(box);
            runBoxes[run] = box;
        }
        return runBoxes[run];
    }

    // ------------------------------------------------------------------ rendering of one step

    function render(step) {
        var empty = document.getElementById('ai-empty');
        if (empty) empty.remove();
        var box = runBox(step.run);
        var d = step.data || {};
        if (step.kind === 'user') {
            var u = el('div', 'ai-msg ai-msg-user');
            u.appendChild(el('div', 'ai-who', W.you));
            var t = el('div', 'ai-text');
            t.innerHTML = md(step.text);
            u.appendChild(t);
            (d.files || []).forEach(function (f) {
                var chip = el('span', 'ai-file' + (f.error ? ' is-error' : ''));
                chip.appendChild(el('i', 'ti ' + (f.kind === 'image' ? 'ti-photo' : 'ti-file-text')));
                chip.appendChild(document.createTextNode(' ' + f.name + (f.error ? ' – ' + f.error : '')));
                if (f.url && f.kind === 'image') chip.title = f.url;
                u.appendChild(chip);
            });
            box.appendChild(u);
        } else if (step.kind === 'assistant') {
            var a = el('div', 'ai-msg ai-msg-ai');
            var who = el('div', 'ai-who', W.ai);
            if (d.model) who.appendChild(el('span', 'ai-meta', ' · ' + d.model));
            if (d.promptTokens || d.completionTokens) who.appendChild(el('span', 'ai-meta', ' · ' + ((d.promptTokens || 0) + (d.completionTokens || 0)).toLocaleString() + ' ' + W.tokens));
            a.appendChild(who);
            if (step.text) {
                var at = el('div', 'ai-text');
                at.innerHTML = md(step.text);
                a.appendChild(at);
            } else if (d.actions) {
                a.appendChild(el('div', 'ai-text muted', W.plans.replace('{0}', d.actions)));
            }
            if (d.note) a.appendChild(el('div', 'ai-note', d.note));
            if (d.raw) a.appendChild(details(W.raw, d.raw));
            box.appendChild(a);
        } else if (step.kind === 'action') {
            var ok = d.ok !== false;
            var warn = (d.warnings || []).length > 0;
            var c = el('div', 'ai-act' + (ok ? (warn ? ' is-warn' : '') : ' is-error'));
            var head = el('div', 'ai-act-head');
            head.appendChild(el('i', 'ti ' + (!ok ? 'ti-alert-circle' : warn ? 'ti-alert-triangle' : (d.changed ? 'ti-pencil' : 'ti-eye'))));
            head.appendChild(el('span', 'ai-act-text', step.text));
            if (step.previewUrl) {
                var go = el('button', 'ai-act-link', W.openPage);
                go.type = 'button';
                go.addEventListener('click', function () { setPreview(step.previewUrl, true); });
                head.appendChild(go);
            }
            c.appendChild(head);
            if (warn) {
                var ul = el('ul', 'ai-warn');
                d.warnings.forEach(function (w) { ul.appendChild(el('li', null, w)); });
                c.appendChild(ul);
            }
            var det = el('details', 'ai-details');
            det.appendChild(el('summary', null, W.details + ' · ' + d.tool));
            det.appendChild(el('div', 'ai-lbl', W.args));
            det.appendChild(el('pre', 'ai-pre', pretty(d.args)));
            det.appendChild(el('div', 'ai-lbl', W.result));
            det.appendChild(el('pre', 'ai-pre', pretty(d.result)));
            c.appendChild(det);
            box.appendChild(c);
            if (step.previewUrl) setPreview(step.previewUrl, !!d.changed);
            else if (d.changed) setPreview(currentUrl, true);
        } else if (step.kind === 'error') {
            var e = el('div', 'ai-line is-error');
            e.appendChild(el('i', 'ti ti-alert-circle'));
            e.appendChild(document.createTextNode(' ' + step.text));
            if (d.raw) e.appendChild(details(W.raw, d.raw));
            box.appendChild(e);
        } else {
            var i = el('div', 'ai-line');
            i.appendChild(el('i', 'ti ti-info-circle'));
            i.appendChild(document.createTextNode(' ' + step.text));
            box.appendChild(i);
            if (d.undo) setPreview(currentUrl, true);
        }
    }

    function setUndo(run) {
        log.querySelectorAll('.ai-undo').forEach(function (b) { b.remove(); });
        if (!run || !runBoxes[run]) return;
        var b = el('button', 'btn btn-ghost btn-sm ai-undo');
        b.type = 'button';
        b.innerHTML = '<i class="ti ti-arrow-back-up"></i> ';
        b.appendChild(document.createTextNode(W.undo));
        b.addEventListener('click', function () {
            MatDialog.confirm(W.undoConfirm, { danger: true }).then(function (yes) {
                if (!yes) return;
                b.disabled = true;
                post(root.dataset.undo, { id: convId }).then(function (r) {
                    if (!r.ok && r.message) MatDialog.alert(r.message);
                    poll();
                });
            });
        });
        runBoxes[run].appendChild(b);
    }

    function setWorking(on) {
        running = on;
        sendBtn.disabled = on;
        stopBtn.hidden = !on;
        if (on && !working) {
            working = el('div', 'ai-line ai-working');
            working.appendChild(el('span', 'ai-spinner'));
            working.appendChild(document.createTextNode(' ' + W.working));
        }
        if (on) log.appendChild(working); else if (working) working.remove();
        scrollDown();
    }

    // ------------------------------------------------------------------ talking to the page

    function post(url, fields, files) {
        var fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        Object.keys(fields).forEach(function (k) { if (fields[k] != null) fd.append(k, fields[k]); });
        (files || []).forEach(function (f) { fd.append('files', f, f.name); });
        return fetch(url, { method: 'POST', body: fd, credentials: 'same-origin' }).then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.json();
        });
    }

    // Reads the new steps; keeps reading while the run goes on.
    function poll() {
        if (!convId) return;
        clearTimeout(pollTimer);
        fetch(root.dataset.steps + '&id=' + convId + '&after=' + lastStep, { credentials: 'same-origin' })
            .then(function (r) { return r.json(); })
            .then(function (res) {
                var atBottom = log.scrollHeight - log.scrollTop - log.clientHeight < 80;
                res.steps.forEach(function (s) { render(s); lastStep = Math.max(lastStep, s.id); });
                setWorking(res.status === 'running');
                setUndo(res.undoRun);
                if (atBottom || res.steps.length) scrollDown();
                if (res.status === 'running') pollTimer = setTimeout(poll, 1200);
            })
            .catch(function () { pollTimer = setTimeout(poll, 3000); });
    }

    function renderFiles() {
        filesBox.innerHTML = '';
        pending.forEach(function (f, i) {
            var chip = el('span', 'ai-file');
            chip.appendChild(el('i', 'ti ' + (/^image\//.test(f.type) ? 'ti-photo' : 'ti-file-text')));
            chip.appendChild(document.createTextNode(' ' + f.name + ' '));
            var x = el('button', 'ai-file-x', '×');
            x.type = 'button';
            x.title = W.remove;
            x.addEventListener('click', function () { pending.splice(i, 1); renderFiles(); });
            chip.appendChild(x);
            filesBox.appendChild(chip);
        });
    }

    document.getElementById('ai-attach').addEventListener('click', function () { fileInput.click(); });
    fileInput.addEventListener('change', function () {
        Array.prototype.forEach.call(fileInput.files, function (f) { pending.push(f); });
        fileInput.value = '';
        renderFiles();
    });

    // Files dropped onto the conversation count as attached.
    ['dragover', 'drop'].forEach(function (ev) {
        root.addEventListener(ev, function (e) {
            if (!e.dataTransfer || !e.dataTransfer.types || Array.prototype.indexOf.call(e.dataTransfer.types, 'Files') < 0) return;
            e.preventDefault();
            if (ev === 'drop') {
                Array.prototype.forEach.call(e.dataTransfer.files, function (f) { pending.push(f); });
                renderFiles();
            }
        });
    });

    log.addEventListener('click', function (e) {
        var ex = e.target.closest('[data-example]');
        if (!ex) return;
        input.value = ex.textContent.trim();
        input.focus();
    });

    form.addEventListener('submit', function (e) {
        e.preventDefault();
        if (running) return;
        var text = input.value.trim();
        if (!text && pending.length === 0) { input.focus(); return; }
        sendBtn.disabled = true;
        var files = pending.slice();
        var fields = { message: text, locked: lock ? String(lock.checked) : 'false' };
        if (convId) fields.id = convId; else { fields.pageId = root.dataset.page || null; fields.blockId = root.dataset.block || null; }
        if (files.length) sendBtn.textContent = W.uploading;
        post(root.dataset.send, fields, files).then(function (r) {
            sendBtn.textContent = form.dataset.sendLabel || sendBtn.textContent;
            if (!r.ok) { sendBtn.disabled = false; MatDialog.alert(r.error || W.sendFailed); return; }
            input.value = '';
            pending = [];
            renderFiles();
            if (!convId) {
                convId = r.id;
                var u = new URL(location.href);
                u.searchParams.set('id', r.id);
                u.searchParams.delete('pageId'); u.searchParams.delete('blockId');
                history.replaceState(null, '', u);
            }
            setWorking(true);
            poll();
        }).catch(function () {
            sendBtn.disabled = false;
            sendBtn.textContent = form.dataset.sendLabel;
            MatDialog.alert(W.sendFailed);
        });
    });
    form.dataset.sendLabel = sendBtn.textContent;

    // Ctrl/Cmd+Enter sends.
    input.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); form.requestSubmit(); }
    });

    stopBtn.addEventListener('click', function () {
        stopBtn.disabled = true;
        post(root.dataset.cancel, { id: convId }).finally(function () { stopBtn.disabled = false; poll(); });
    });

    document.getElementById('ai-reload').addEventListener('click', function () { setPreview(currentUrl, true); });

    if (convId) poll();
})();
