// One AI profile of the cloud: only the fields of the chosen kind, the address presets, "Modelle laden" (with "nur kostenlose")
// and "Testen" — the last two send what is in the form, before it is saved.
(function () {
    'use strict';
    var form = document.getElementById('aip-form');
    if (!form) return;

    function post(url) {
        return fetch(url, { method: 'POST', body: new FormData(form), credentials: 'same-origin' }).then(function (r) { return r.json(); });
    }

    function showParts() {
        var kind = (form.querySelector('input[name="kind"]:checked') || {}).value || 'openai';
        form.querySelectorAll('[data-aip-part]').forEach(function (p) { p.hidden = p.getAttribute('data-aip-part') !== kind; });
        // The sign-in card stands outside the form (it has forms of its own); it belongs to the ChatGPT kind too.
        document.querySelectorAll('[data-for-kind]').forEach(function (c) { c.hidden = c.getAttribute('data-for-kind') !== kind; });
    }
    form.querySelectorAll('input[name="kind"]').forEach(function (r) { r.addEventListener('change', showParts); });
    showParts();

    var preset = document.getElementById('aip-preset'), base = document.getElementById('aip-base'), model = document.getElementById('aip-model');
    preset.addEventListener('change', function () {
        var o = preset.options[preset.selectedIndex];
        var url = o.getAttribute('data-url') || '';
        base.value = url;
        if (!url) base.focus();
        model.placeholder = o.getAttribute('data-model') || '';
    });

    var load = document.getElementById('aip-load'), freeOnly = document.getElementById('aip-free');
    var list = document.getElementById('aip-models-list'), info = document.getElementById('aip-models-info');
    var models = [];
    function fill() {
        list.innerHTML = '';
        var shown = models.filter(function (m) { return !freeOnly.checked || m.free; });
        shown.forEach(function (m) {
            var o = document.createElement('option');
            o.value = m.id;
            o.label = (m.name !== m.id ? m.name : '') + (m.free ? ' · ' + info.getAttribute('data-free') : '');
            list.appendChild(o);
        });
        info.textContent = models.length ? shown.length + ' / ' + models.length : '';
    }
    load.addEventListener('click', function () {
        load.disabled = true;
        info.textContent = '…';
        post(load.getAttribute('data-url')).then(function (r) {
            if (!r.ok) { models = []; list.innerHTML = ''; info.textContent = '✗ ' + (r.error || ''); return; }
            models = r.models || [];
            fill();
            model.focus();
        }).catch(function () { info.textContent = '✗'; }).finally(function () { load.disabled = false; });
    });
    freeOnly.addEventListener('change', fill);

    var test = document.getElementById('aip-test'), out = document.getElementById('aip-test-result');
    test.addEventListener('click', function () {
        test.disabled = true;
        out.textContent = '…';
        post(test.getAttribute('data-url')).then(function (r) {
            out.textContent = r.ok ? '✓ ' + (r.model ? r.model + ': ' : '') + r.text : '✗ ' + (r.error || '');
        }).catch(function () { out.textContent = '✗'; }).finally(function () { test.disabled = false; });
    });
})();
