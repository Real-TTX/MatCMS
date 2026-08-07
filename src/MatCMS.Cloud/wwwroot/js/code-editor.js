// Upgrades any <textarea data-code="html|css|js|json"> into a CodeMirror editor.
//
// The textarea stays the source of truth: CodeMirror writes back into it on every change, so normal
// form posts keep working and nothing here needs to know about the surrounding form. Progressive
// enhancement — without JS (or without the CodeMirror bundle) the plain textarea still works.
(function () {
    'use strict';

    var MODES = {
        html: 'htmlmixed',
        css: 'css',
        js: 'javascript',
        json: { name: 'javascript', json: true }
    };

    function upgrade(textarea) {
        if (textarea.dataset.codeReady === '1') return;
        textarea.dataset.codeReady = '1';

        var kind = (textarea.dataset.code || 'html').toLowerCase();
        var editor = CodeMirror.fromTextArea(textarea, {
            mode: MODES[kind] || MODES.html,
            lineNumbers: true,
            autoCloseBrackets: true,
            matchBrackets: true,
            styleActiveLine: true,
            lineWrapping: true,
            indentUnit: 2,
            tabSize: 2,
            // Tab inserts spaces instead of moving focus — in a code field that is what you want,
            // and Esc still gets you out for keyboard navigation.
            extraKeys: {
                Tab: function (cm) { cm.replaceSelection('  '); },
                Esc: function (cm) { cm.getInputField().blur(); }
            }
        });

        var rows = parseInt(textarea.getAttribute('rows') || '10', 10);
        editor.setSize(null, Math.max(120, rows * 22));
        // Keep the textarea in sync so a plain form submit carries the current content.
        editor.on('change', function () { editor.save(); });

        // JSON fields validate as you type: a broken field definition would otherwise only surface
        // when the server rejects it, or worse, on the instance during a sync.
        if (kind === 'json') {
            var status = document.createElement('div');
            status.className = 'code-status';
            editor.getWrapperElement().parentNode.insertBefore(status, editor.getWrapperElement().nextSibling);

            var check = function () {
                var value = editor.getValue().trim();
                if (value === '') { status.textContent = ''; status.className = 'code-status'; return; }
                try {
                    JSON.parse(value);
                    status.textContent = 'JSON ok';
                    status.className = 'code-status is-ok';
                } catch (e) {
                    status.textContent = e.message;
                    status.className = 'code-status is-error';
                }
            };
            editor.on('change', check);
            check();
        }

        // A field inside a hidden tab panel measures as zero-height; refresh once it is shown.
        var panel = textarea.closest('.tab-panel');
        if (panel) {
            new MutationObserver(function () {
                if (!panel.hidden) editor.refresh();
            }).observe(panel, { attributes: true, attributeFilter: ['hidden'] });
        }
    }

    function init() {
        if (typeof CodeMirror === 'undefined') return;
        document.querySelectorAll('textarea[data-code]').forEach(upgrade);
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();
