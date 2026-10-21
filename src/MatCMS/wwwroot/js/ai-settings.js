// Einstellungen → KI: which connection the site uses (cloud / own API key / own ChatGPT account), "Testen", and the ChatGPT
// sign-in by device code — start it, show the code and the link, poll until it is confirmed, then offer the account's models.
(function () {
    'use strict';
    var form = document.getElementById('ai-form');
    if (!form) return;
    var token = form.querySelector('input[name="__RequestVerificationToken"]').value;

    function post(url) {
        var fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        return fetch(url, { method: 'POST', body: fd, credentials: 'same-origin' }).then(function (r) { return r.json(); });
    }

    // Only the fields of the chosen way are shown.
    function showParts() {
        var chosen = (form.querySelector('input[name="provider"]:checked') || {}).value || '';
        form.querySelectorAll('[data-ai-part]').forEach(function (p) { p.hidden = p.getAttribute('data-ai-part') !== chosen; });
        if (chosen === 'chatgpt') refresh();
    }
    form.querySelectorAll('input[name="provider"]').forEach(function (r) { r.addEventListener('change', showParts); });

    var test = document.getElementById('ai-test'), testOut = document.getElementById('ai-test-result');
    if (test) test.addEventListener('click', function () {
        test.disabled = true;
        testOut.textContent = '…';
        post(test.getAttribute('data-url')).then(function (r) {
            testOut.textContent = r.ok ? '✓ ' + (r.model ? r.model + ': ' : '') + r.text : '✗ ' + (r.error || '');
        }).catch(function () { testOut.textContent = '✗'; }).finally(function () { test.disabled = false; });
    });

    // ---- ChatGPT ----
    var box = document.getElementById('ai-chatgpt');
    if (!box) { showParts(); return; }
    var W = JSON.parse(box.getAttribute('data-words') || '{}');
    var state = document.getElementById('ai-cg-state'), pending = document.getElementById('ai-cg-pending');
    var select = document.getElementById('ai-cg-model'), login = document.getElementById('ai-cg-login'), logout = document.getElementById('ai-cg-logout');
    var timer = null, wanted = box.getAttribute('data-model') || '';

    function render(s) {
        clearTimeout(timer);
        state.textContent = s.connected
            ? W.connected.replace('{0}', s.email || '?') + (s.plan ? ' · ' + W.plan.replace('{0}', s.plan) : '')
            : (s.error || s.loginError || W.notConnected);
        logout.hidden = !s.connected;
        login.hidden = !!s.pending;
        if (s.pending) {
            pending.hidden = false;
            pending.innerHTML = '';
            pending.appendChild(document.createTextNode(W.code + ' '));
            var code = document.createElement('strong');
            code.textContent = s.pending.userCode;
            pending.appendChild(code);
            pending.appendChild(document.createTextNode(' · '));
            var a = document.createElement('a');
            a.href = s.pending.verificationUrl;
            a.target = '_blank';
            a.rel = 'noopener';
            a.textContent = W.open;
            pending.appendChild(a);
            pending.appendChild(document.createTextNode(' · ' + W.waiting));
            timer = setTimeout(refresh, Math.max(3, s.pending.intervalSeconds || 5) * 1000);
        } else pending.hidden = true;
        if (s.models && s.models.length) {
            var keep = select.value || wanted;
            select.innerHTML = '';
            s.models.forEach(function (m) {
                var o = document.createElement('option');
                o.value = m.slug;
                o.textContent = m.name;
                select.appendChild(o);
            });
            select.value = s.models.some(function (m) { return m.slug === keep; }) ? keep : s.models[0].slug;
        }
    }

    function refresh() {
        fetch(box.getAttribute('data-status'), { credentials: 'same-origin' }).then(function (r) { return r.json(); }).then(render)
            .catch(function () { timer = setTimeout(refresh, 5000); });
    }

    login.addEventListener('click', function () {
        login.disabled = true;
        post(box.getAttribute('data-login')).then(function (r) {
            if (!r.ok && r.error) state.textContent = r.error;
            render(r.status);
        }).finally(function () { login.disabled = false; });
    });
    logout.addEventListener('click', function () {
        MatDialog.confirm(logout.textContent + '?', { danger: true }).then(function (yes) {
            if (yes) post(box.getAttribute('data-logout')).then(refresh);
        });
    });

    showParts();
})();
