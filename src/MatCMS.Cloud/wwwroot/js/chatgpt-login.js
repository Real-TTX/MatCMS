// "Mit ChatGPT anmelden" (Einstellungen → KI): starts the device-code login, shows the code, and asks the status handler — which
// asks OpenAI at most once per poll interval — until the code was confirmed. Wording comes in as data- attributes of the card.
(function () {
    'use strict';
    var root = document.querySelector('[data-chatgpt]');
    var startBtn = root && root.querySelector('[data-chatgpt-start]');
    if (!root || !startBtn) return;   // connected (or no card): nothing to drive

    var box = root.querySelector('[data-chatgpt-pending]');
    var codeEl = root.querySelector('[data-chatgpt-code]');
    var link = root.querySelector('[data-chatgpt-link]');
    var left = root.querySelector('[data-chatgpt-left]');
    var copyBtn = root.querySelector('[data-chatgpt-copy]');
    var errBox = root.querySelector('[data-chatgpt-error]');
    var antiforgery = document.querySelector('input[name="__RequestVerificationToken"]');
    var poller = null, ticker = null, expires = 0;

    function stop() { clearTimeout(poller); clearInterval(ticker); poller = ticker = null; }
    function fail(message) { stop(); box.hidden = true; startBtn.disabled = false; errBox.textContent = message || ''; errBox.hidden = !message; }

    function tick() {
        var s = Math.max(0, Math.round((expires - Date.now()) / 1000));
        left.textContent = root.dataset.tValid + ' ' + Math.floor(s / 60) + ':' + ('0' + (s % 60)).slice(-2);
        if (s === 0) fail(root.dataset.tExpired);
    }

    function show(p) {
        codeEl.textContent = p.userCode;
        link.href = p.verificationUrl;
        expires = Date.parse(p.expiresAt);
        box.hidden = false;
        tick();
        if (!ticker) ticker = setInterval(tick, 1000);
    }

    function poll(delaySeconds) {
        poller = setTimeout(function () {
            fetch(root.dataset.statusUrl, { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
                .then(function (r) { return r.json(); })
                .then(function (s) {
                    if (s.connected) { stop(); location.href = location.pathname + '?tab=ai'; return; }
                    if (s.pending) { show(s.pending); poll(Math.max(3, s.pending.intervalSeconds || 5)); return; }
                    fail(s.loginError || s.error || root.dataset.tExpired);
                })
                .catch(function () { poll(5); });   // a hiccup between page and cloud must not end a login that is still valid
        }, delaySeconds * 1000);
    }

    // A page reload in the middle of a login: the cloud still holds the running login, so show its code again and keep asking.
    if (root.dataset.pendingCode) {
        startBtn.disabled = true;
        show({ userCode: root.dataset.pendingCode, verificationUrl: root.dataset.pendingUrl, expiresAt: root.dataset.pendingExpires });
        poll(Math.max(3, parseInt(root.dataset.pendingInterval, 10) || 5));
    }

    startBtn.addEventListener('click', function () {
        stop(); errBox.hidden = true; startBtn.disabled = true;
        fetch(root.dataset.startUrl, {
            method: 'POST', credentials: 'same-origin',
            headers: { 'Accept': 'application/json', 'RequestVerificationToken': antiforgery ? antiforgery.value : '' }
        })
            .then(function (r) { return r.json(); })
            .then(function (j) {
                if (!j.ok) { fail(j.error); return; }
                show(j.status.pending);
                poll(Math.max(3, j.status.pending.intervalSeconds || 5));
            })
            .catch(function () { fail('Request failed'); });
    });

    copyBtn.addEventListener('click', function () {
        if (!navigator.clipboard) return;
        navigator.clipboard.writeText(codeEl.textContent).then(function () {
            copyBtn.textContent = root.dataset.tCopied;
            setTimeout(function () { copyBtn.textContent = root.dataset.tCopy; }, 1500);
        });
    });
})();
