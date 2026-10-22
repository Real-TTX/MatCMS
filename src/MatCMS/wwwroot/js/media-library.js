// The media library: upload, the INFO dialog (facts, where it is used, alt text, tags), the IMAGE EDITOR (crop, rotate, flip,
// size, focal point — done in the browser with Cropper.js, stored by the server in place or as a copy) and the SVG editor
// (the code, cleaned by the server). Every item carries its facts as JSON in data-media.
(function () {
    'use strict';
    var root = document.getElementById('media-lib');
    if (!root) return;
    var W = JSON.parse(root.getAttribute('data-words') || '{}');
    var token = root.querySelector('input[name="__RequestVerificationToken"]').value;
    var current = null;   // the item the dialogs work on

    function $(id) { return document.getElementById(id); }
    function fmtBytes(n) {
        n = parseInt(n, 10) || 0;
        if (n < 1024) return n + ' B';
        if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
        return (n / 1048576).toFixed(1) + ' MB';
    }
    function bust(url) { return url + (url.indexOf('?') >= 0 ? '&' : '?') + 't=' + Date.now(); }
    function post(url, fields, file, fileName) {
        var fd = new FormData();
        fd.append('__RequestVerificationToken', token);
        Object.keys(fields || {}).forEach(function (k) { if (fields[k] != null) fd.append(k, fields[k]); });
        if (file) fd.append('file', file, fileName || 'bild');
        return fetch(url, { method: 'POST', body: fd, credentials: 'same-origin' }).then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.json();
        });
    }
    function modal(id) {
        var m = $(id);
        m.querySelectorAll('[data-close]').forEach(function (b) { b.addEventListener('click', function () { close(m); }); });
        m.addEventListener('click', function (e) { if (e.target === m) close(m); });
        return m;
    }
    var closeHooks = new Map();
    function open(m) { m.classList.add('open'); }
    function close(m) {
        var hook = closeHooks.get(m);
        if (hook && hook() === false) return;
        m.classList.remove('open');
    }
    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape') return;
        var top = document.querySelectorAll('.modal-overlay.open');
        if (top.length) close(top[top.length - 1]);
    });

    // ------------------------------------------------------------------ upload
    var upBtn = $('media-upload-btn'), upFile = $('media-file');
    upBtn.addEventListener('click', function () { upFile.click(); });
    upFile.addEventListener('change', function () {
        if (!upFile.files || !upFile.files.length) return;
        upBtn.disabled = true; upBtn.textContent = W.uploading;
        var chain = Promise.resolve();
        Array.prototype.forEach.call(upFile.files, function (f) {
            chain = chain.then(function () {
                var fd = new FormData(); fd.append('file', f);
                return fetch('/admin/api/upload', { method: 'POST', body: fd })
                    .then(function (r) { return r.json().then(function (j) { return { ok: r.ok, j: j }; }); })
                    .then(function (r) { if (!r.ok) return MatDialog.alert(r.j.error || W.uploadFailed); });
            });
        });
        chain.then(function () { location.reload(); });
    });

    // ------------------------------------------------------------------ info dialog
    var info = modal('media-modal');
    var tags = tagPicker();
    var changed = false;    // something was edited: the grid is reloaded when the info closes

    function kindLabel(k) { return { image: W.kindImage, svg: W.kindSvg, gif: W.kindGif }[k] || W.kindFile; }
    function showInfo(d) {
        current = d;
        var isPic = d.kind !== 'file';
        $('mm-img').hidden = !isPic;
        $('mm-ext').hidden = isPic;
        if (isPic) { $('mm-img').src = bust(d.url); $('mm-img').alt = d.alt || ''; }
        else $('mm-ext').textContent = (d.name.split('.').pop() || 'FILE').toUpperCase();
        $('mm-preview').classList.toggle('med-preview-check', d.kind === 'svg' || /\.png$|\.webp$|\.gif$/i.test(d.url));
        $('mm-name').textContent = d.name || '';
        $('mm-kind').textContent = kindLabel(d.kind) + (d.type ? ' · ' + d.type : '');
        $('mm-dims').textContent = d.width ? d.width + ' × ' + d.height + ' px' : '–';
        $('mm-size').textContent = fmtBytes(d.size);
        $('mm-created').textContent = d.created ? new Date(d.created + (/Z$/.test(d.created) ? '' : 'Z')).toLocaleString() : '';
        $('mm-focal-row').hidden = !isPic;
        $('mm-focal-value').textContent = d.focalX != null ? d.focalX + ' % / ' + d.focalY + ' %' : W.focalCenter;
        $('mm-edited-row').hidden = d.kind !== 'image';
        $('mm-edited').textContent = d.hasOriginal ? W.edited : W.notEdited;
        $('mm-url').value = d.url;
        $('mm-alt').value = d.alt || '';
        tags.set(d.tags || '');
        $('mm-id').value = d.id; $('mm-del-id').value = d.id;
        $('mm-edit').hidden = d.kind !== 'image';
        $('mm-edit-svg').hidden = d.kind !== 'svg';
        $('mm-focal').hidden = !(d.kind === 'svg' || d.kind === 'gif');
        $('mm-download').href = d.url;
        $('mm-download').setAttribute('download', d.name || '');
        $('mm-replace-file').accept = d.kind === 'image' ? 'image/jpeg,image/png,image/webp' : d.kind === 'svg' ? '.svg' : d.kind === 'gif' ? '.gif' : '.' + (d.url.split('.').pop());
        loadUses(d);
        open(info);
    }
    closeHooks.set(info, function () { if (changed) location.reload(); return true; });

    function loadUses(d) {
        var ul = $('mm-uses');
        ul.innerHTML = '';
        ul.appendChild(el('li', 'muted', W.usesLoading));
        fetch(root.getAttribute('data-uses') + '&id=' + d.id, { credentials: 'same-origin' }).then(function (r) { return r.json(); }).then(function (list) {
            ul.innerHTML = '';
            d.uses = list.length;
            if (!list.length) { ul.appendChild(el('li', 'muted', W.noUses)); return; }
            list.forEach(function (u) {
                var li = el('li');
                var kind = { page: W.usePage, template: W.useTemplate, setting: W.useSetting, menu: W.useMenu, form: W.useForm, component: W.useComponent, plugin: W.usePlugin }[u.kind] || '';
                li.appendChild(el('span', 'mini-chip', kind));
                li.appendChild(document.createTextNode(' '));
                if (u.link) { var a = el('a', null, u.title); a.href = u.link; li.appendChild(a); }
                else li.appendChild(document.createTextNode(u.title));
                ul.appendChild(li);
            });
        }).catch(function () { ul.innerHTML = ''; });
    }
    function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }

    document.querySelectorAll('.media-card, .media-row').forEach(function (c) {
        c.addEventListener('click', function () { showInfo(JSON.parse(c.getAttribute('data-media'))); });
    });

    // A picture still on pages is worth one more sentence before it goes.
    $('mm-delete-form').addEventListener('submit', function (e) {
        if (!current || !current.uses) return;
        if (this.dataset.usedOk === '1') return;
        e.preventDefault(); e.stopImmediatePropagation();
        var form = this;
        MatDialog.confirm(W.deleteUsed.replace('{0}', current.uses), { danger: true }).then(function (yes) {
            if (yes) { form.dataset.usedOk = '1'; form.removeAttribute('data-confirm'); form.submit(); }
        });
    }, true);

    $('mm-copy').addEventListener('click', function () {
        var b = this, u = location.origin + $('mm-url').value;
        (navigator.clipboard ? navigator.clipboard.writeText(u) : Promise.reject()).then(function () {
            var t = b.textContent; b.textContent = W.copied; setTimeout(function () { b.textContent = t; }, 1200);
        }).catch(function () { $('mm-url').select(); document.execCommand('copy'); });
    });

    // Replace the file: same address, new content.
    $('mm-replace').addEventListener('click', function () { $('mm-replace-file').click(); });
    $('mm-replace-file').addEventListener('change', function () {
        var f = this.files && this.files[0];
        this.value = '';
        if (!f || !current) return;
        MatDialog.confirm(W.replaceConfirm).then(function (yes) {
            if (!yes) return;
            post(root.getAttribute('data-replace'), { id: current.id }, f, f.name).then(function (r) {
                if (!r.ok) return MatDialog.alert(r.error || W.saveFailed);
                changed = true;
                showInfo(r.media);
            }).catch(function () { MatDialog.alert(W.saveFailed); });
        });
    });

    function tagPicker() {
        var hidden = $('mm-tags'), box = $('mm-tag-input'), entry = $('mm-tag-entry'), add = $('mm-tag-add'), chips = [];
        function showEntry() { add.hidden = true; entry.hidden = false; entry.focus(); }
        function hideEntry() { entry.hidden = true; add.hidden = false; }
        add.addEventListener('click', showEntry);
        function render() {
            box.querySelectorAll('.tag-chip-item').forEach(function (n) { n.remove(); });
            chips.forEach(function (tag) {
                var s = el('span', 'tag-chip-item', tag);
                var x = el('button', 'tag-x', '×'); x.type = 'button';
                x.addEventListener('click', function () { chips = chips.filter(function (c) { return c !== tag; }); render(); });
                s.appendChild(x);
                box.insertBefore(s, entry);
            });
            hidden.value = chips.join(', ');
        }
        function addTag(raw) {
            var t = (raw || '').replace(/,/g, ' ').trim();
            if (!t || chips.some(function (c) { return c.toLowerCase() === t.toLowerCase(); })) return;
            chips.push(t); render();
        }
        entry.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); addTag(entry.value); entry.value = ''; }
            else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); entry.value = ''; hideEntry(); }
            else if (e.key === 'Backspace' && !entry.value && chips.length) { chips.pop(); render(); }
        });
        entry.addEventListener('blur', function () { if (entry.value.trim()) { addTag(entry.value); entry.value = ''; } hideEntry(); });
        $('mm-save-form').addEventListener('submit', function () { if (entry.value.trim()) addTag(entry.value); });
        var suggest = $('mm-suggest');
        if (suggest) suggest.addEventListener('click', function (e) { var c = e.target.closest('.mini-chip-add'); if (c) addTag(c.getAttribute('data-tag')); });
        return {
            set: function (str) {
                var seen = {};
                chips = (str || '').split(',').map(function (s) { return s.trim(); }).filter(function (c) {
                    if (!c) return false; var k = c.toLowerCase(); if (seen[k]) return false; seen[k] = 1; return true;
                });
                entry.value = ''; hideEntry(); render();
            }
        };
    }

    // ------------------------------------------------------------------ the image editor
    var ed = modal('med-editor');
    var edImg = $('me-img'), cropper = null, ctx = null, tab = 'crop';
    var focal = { x: null, y: null, img: $('me-focal-img'), dot: $('me-focal-dot') };

    function editOpen(d, onlyFocal) {
        current = d;
        ctx = { d: d, start: null, dirtyGeometry: false, onlyFocal: !!onlyFocal, ratio: 'free', outW: 0 };
        focal.x = d.focalX; focal.y = d.focalY;
        $('me-title').textContent = (onlyFocal ? '' : '') + d.name;
        ed.querySelectorAll('[data-me-tab]').forEach(function (t) { t.hidden = onlyFocal && t.getAttribute('data-me-tab') !== 'focal'; });
        $('me-reset').hidden = !d.hasOriginal || onlyFocal;
        $('me-copy').hidden = onlyFocal;
        $('me-from-original').hidden = !d.hasOriginal;
        open(ed);
        if (onlyFocal) { showTab('focal'); focalImageFrom(bust(d.url)); return; }
        showTab('crop');
        // The edit always starts from the ORIGINAL (when an earlier "Übernehmen" set one aside) with its last settings.
        var src = d.hasOriginal && d.original ? d.original : d.url;
        if (cropper) { cropper.destroy(); cropper = null; }
        edImg.onload = function () {
            edImg.onload = null;
            // One frame later: the dialog has its final size by then, and Cropper.js measures its box only once.
            requestAnimationFrame(function () { start(); });
        };
        function start() {
            cropper = new Cropper(edImg, {
                viewMode: 1, autoCropArea: 1, background: true, responsive: true, zoomable: false, movable: false, toggleDragModeOnDblclick: false,
                ready: function () {
                    var last = null;
                    try { last = d.edit ? JSON.parse(d.edit) : null; } catch (e) { last = null; }
                    if (last && d.hasOriginal) {
                        if (last.rotate) cropper.rotateTo(last.rotate);
                        if (last.scaleX === -1) cropper.scaleX(-1);
                        if (last.scaleY === -1) cropper.scaleY(-1);
                        if (last.width) cropper.setData({ x: last.x, y: last.y, width: last.width, height: last.height });
                        ctx.outW = last.outW || 0;
                    }
                    ctx.start = JSON.stringify(state());
                    syncSize();
                },
                crop: function () { syncSize(); }
            });
        }
        edImg.src = bust(src);
    }
    closeHooks.set(ed, function () {
        if (cropper && ctx && !ctx.onlyFocal && JSON.stringify(state()) !== ctx.start && !ctx.closing) {
            MatDialog.confirm(W.leaveConfirm).then(function (yes) { if (yes) { ctx.closing = true; close(ed); } });
            return false;
        }
        if (cropper) { cropper.destroy(); cropper = null; }
        return true;
    });

    function state() {
        var dt = cropper ? cropper.getData(true) : {};
        return { x: dt.x, y: dt.y, width: dt.width, height: dt.height, rotate: dt.rotate || 0, scaleX: dt.scaleX || 1, scaleY: dt.scaleY || 1, outW: ctx.outW || 0 };
    }
    function naturalSize() {
        var im = cropper.getImageData();
        return { w: Math.round(im.naturalWidth), h: Math.round(im.naturalHeight) };
    }
    /// True when the geometry is the untouched picture (no crop, no turn, no flip, no resize).
    function untouched() {
        var s = state(), n = naturalSize();
        var turned = (s.rotate % 180) !== 0;
        var fw = turned ? n.h : n.w, fh = turned ? n.w : n.h;
        return s.rotate % 360 === 0 && s.scaleX === 1 && s.scaleY === 1 && s.x <= 1 && s.y <= 1 && Math.abs(s.width - fw) <= 2 && Math.abs(s.height - fh) <= 2
            && (!s.outW || s.outW >= s.width) && !current.hasOriginal;
    }

    function showTab(name) {
        tab = name;
        ed.querySelectorAll('[data-me-tab]').forEach(function (t) { t.classList.toggle('active', t.getAttribute('data-me-tab') === name); });
        ed.querySelectorAll('[data-me-panel]').forEach(function (p) { p.hidden = p.getAttribute('data-me-panel') !== name; });
        $('me-crop-wrap').hidden = name === 'focal';
        $('me-focal-wrap').hidden = name !== 'focal';
        // The focal point belongs to the FINISHED picture: the focal tab shows the current result, not the original.
        if (name === 'focal' && cropper) {
            var c = cropper.getCroppedCanvas({ maxWidth: 1400, maxHeight: 1400, imageSmoothingQuality: 'high' });
            if (c) focalImageFrom(c.toDataURL('image/jpeg', 0.85));
        }
    }
    ed.querySelectorAll('[data-me-tab]').forEach(function (t) { t.addEventListener('click', function () { showTab(t.getAttribute('data-me-tab')); }); });

    // ---- crop & turn ----
    $('me-ratios').addEventListener('click', function (e) {
        var b = e.target.closest('[data-ratio]');
        if (!b || !cropper) return;
        $('me-ratios').querySelectorAll('.med-chip').forEach(function (c) { c.classList.toggle('is-active', c === b); });
        var r = b.getAttribute('data-ratio');
        if (r === 'free') cropper.setAspectRatio(NaN);
        else if (r === 'orig') { var n = naturalSize(); cropper.setAspectRatio(n.w / n.h); }
        else cropper.setAspectRatio(parseFloat(r));
    });
    ed.querySelector('[data-me-panel="crop"]').addEventListener('click', function (e) {
        var b = e.target.closest('[data-op]');
        if (!b || !cropper) return;
        var op = b.getAttribute('data-op'), d = cropper.getData();
        if (op === 'rotL') cropper.rotate(-90);
        if (op === 'rotR') cropper.rotate(90);
        if (op === 'flipH') cropper.scaleX(d.scaleX === -1 ? 1 : -1);
        if (op === 'flipV') cropper.scaleY(d.scaleY === -1 ? 1 : -1);
        if (op === 'clear') { cropper.reset(); cropper.setAspectRatio(NaN); ctx.outW = 0; $('me-ratios').querySelectorAll('.med-chip').forEach(function (c, i) { c.classList.toggle('is-active', i === 0); }); }
        syncSize();
    });

    // ---- size: the output width; the height follows the crop's proportions ----
    var wIn = $('me-w'), hIn = $('me-h');
    function cropBox() { var s = state(); return { w: Math.max(1, s.width), h: Math.max(1, s.height) }; }
    function syncSize() {
        if (!cropper || !ctx) return;
        var b = cropBox();
        var w = ctx.outW && ctx.outW < b.w ? ctx.outW : b.w;
        var h = Math.round(w * b.h / b.w);
        if (document.activeElement !== wIn) wIn.value = w;
        if (document.activeElement !== hIn) hIn.value = h;
        $('me-output').textContent = W.output.replace('{0}', w + ' × ' + h);
    }
    wIn.addEventListener('input', function () { var b = cropBox(), v = parseInt(wIn.value, 10) || 0; ctx.outW = v > 0 && v < b.w ? v : 0; hIn.value = Math.round((ctx.outW || b.w) * b.h / b.w); syncSize(); });
    hIn.addEventListener('input', function () { var b = cropBox(), v = parseInt(hIn.value, 10) || 0; var w = Math.round(v * b.w / b.h); ctx.outW = w > 0 && w < b.w ? w : 0; wIn.value = ctx.outW || b.w; syncSize(); });
    $('me-presets').addEventListener('click', function (e) {
        var b = e.target.closest('[data-w]');
        if (!b || !cropper) return;
        var v = parseInt(b.getAttribute('data-w'), 10), box = cropBox();
        ctx.outW = v > 0 && v < box.w ? v : 0;
        syncSize();
    });

    // ---- focal point: a click on the finished picture; three crops show what it does ----
    function focalImageFrom(src) {
        focal.img.onload = function () { paintFocal(); };
        focal.img.src = src;
    }
    function paintFocal() {
        var has = focal.x != null && focal.y != null;
        focal.dot.hidden = !has;
        if (has) {
            focal.dot.style.left = (focal.img.offsetLeft + focal.img.offsetWidth * focal.x / 100) + 'px';
            focal.dot.style.top = (focal.img.offsetTop + focal.img.offsetHeight * focal.y / 100) + 'px';
        }
        var pos = has ? focal.x + '% ' + focal.y + '%' : '50% 50%';
        ed.querySelectorAll('.med-fp').forEach(function (p) {
            p.style.aspectRatio = p.getAttribute('data-ar');
            p.style.backgroundImage = 'url("' + focal.img.src + '")';
            p.style.backgroundPosition = pos;
        });
    }
    focal.img.addEventListener('click', function (e) {
        var r = focal.img.getBoundingClientRect();
        focal.x = Math.max(0, Math.min(100, Math.round((e.clientX - r.left) / r.width * 100)));
        focal.y = Math.max(0, Math.min(100, Math.round((e.clientY - r.top) / r.height * 100)));
        paintFocal();
    });
    $('me-focal-reset').addEventListener('click', function () { focal.x = null; focal.y = null; paintFocal(); });
    window.addEventListener('resize', function () { if (tab === 'focal') paintFocal(); });

    // ---- saving ----
    function mimeOf(url) { return /\.png$/i.test(url) ? 'image/png' : /\.webp$/i.test(url) ? 'image/webp' : 'image/jpeg'; }
    function result() {
        var s = state(), mime = mimeOf(current.url);
        var opts = { imageSmoothingEnabled: true, imageSmoothingQuality: 'high' };
        if (s.outW && s.outW < s.width) { opts.width = s.outW; opts.height = Math.round(s.outW * s.height / s.width); }
        if (mime === 'image/jpeg') opts.fillColor = '#fff';   // a JPEG has no transparency; a turned corner must not come out black
        var canvas = cropper.getCroppedCanvas(opts);
        return new Promise(function (res) { canvas.toBlob(res, mime, 0.92); });
    }
    function saveFocal(id) {
        var same = (focal.x == null ? null : focal.x) === (current.focalX == null ? null : current.focalX) && (focal.y == null ? null : focal.y) === (current.focalY == null ? null : current.focalY);
        if (same) return Promise.resolve({ ok: true });
        return post(root.getAttribute('data-focal'), { id: id, focalX: focal.x, focalY: focal.y });
    }
    function busy(on) { ['me-apply', 'me-copy', 'me-reset'].forEach(function (i) { $(i).disabled = on; }); $('me-apply').textContent = on ? W.saving : $('me-apply').dataset.label; }
    $('me-apply').dataset.label = $('me-apply').textContent;
    function finish(media) { changed = true; ctx.closing = true; if (cropper) { cropper.destroy(); cropper = null; } ed.classList.remove('open'); if (media) showInfo(media); }

    function save(asCopy) {
        if (!current) return;
        busy(true);
        var geometry = cropper && !ctx.onlyFocal && !(untouched() && !asCopy) && (JSON.stringify(state()) !== ctx.start || asCopy);
        var step = geometry
            ? result().then(function (blob) { return post(root.getAttribute('data-edit'), { id: current.id, mode: asCopy ? 'copy' : 'replace', edit: JSON.stringify(state()) }, blob, current.name); })
            : Promise.resolve({ ok: true, media: current });
        step.then(function (r) {
            if (!r.ok) throw new Error(r.error);
            return saveFocal(r.media.id).then(function (f) { if (!f.ok) throw new Error(f.error); return f.media || r.media; });
        }).then(finish).catch(function (e) { MatDialog.alert((e && e.message && !/^HTTP/.test(e.message) ? e.message : W.saveFailed)); })
          .finally(function () { busy(false); });
    }
    $('me-apply').addEventListener('click', function () { save(false); });
    $('me-copy').addEventListener('click', function () { save(true); });
    $('me-reset').addEventListener('click', function () {
        MatDialog.confirm(W.resetConfirm, { danger: true }).then(function (yes) {
            if (!yes) return;
            post(root.getAttribute('data-reset'), { id: current.id }).then(function (r) {
                if (!r.ok) return MatDialog.alert(r.error || W.saveFailed);
                finish(r.media);
            });
        });
    });

    $('mm-edit').addEventListener('click', function () { editOpen(current, false); });
    $('mm-focal').addEventListener('click', function () { editOpen(current, true); });

    // ------------------------------------------------------------------ the SVG editor
    var sv = modal('med-svg'), code = $('ms-code'), svImg = $('ms-img'), svErr = $('ms-error'), svUrl = null, svStart = '';
    function svPreview() {
        if (svUrl) URL.revokeObjectURL(svUrl);
        var ok = /^\s*(<\?xml[^>]*>\s*)?<svg[\s>]/i.test(code.value);
        svErr.hidden = ok;
        svErr.textContent = ok ? '' : W.svgInvalid;
        svUrl = URL.createObjectURL(new Blob([code.value], { type: 'image/svg+xml' }));
        svImg.src = svUrl;
    }
    var svTimer = null;
    code.addEventListener('input', function () { clearTimeout(svTimer); svTimer = setTimeout(svPreview, 250); });
    $('mm-edit-svg').addEventListener('click', function () {
        fetch(root.getAttribute('data-svg') + '&id=' + current.id, { credentials: 'same-origin' }).then(function (r) { return r.text(); }).then(function (t) {
            code.value = t; svStart = t; svPreview(); open(sv); code.focus();
        });
    });
    closeHooks.set(sv, function () {
        if (code.value !== svStart && !sv.dataset.closing) {
            MatDialog.confirm(W.leaveConfirm).then(function (yes) { if (yes) { sv.dataset.closing = '1'; close(sv); delete sv.dataset.closing; } });
            return false;
        }
        return true;
    });
    $('ms-save').addEventListener('click', function () {
        post(root.getAttribute('data-svg'), { id: current.id, code: code.value }).then(function (r) {
            if (!r.ok) { svErr.hidden = false; svErr.textContent = r.error || W.saveFailed; return; }
            svStart = code.value = r.clean;
            changed = true;
            sv.classList.remove('open');
            showInfo(r.media);
        }).catch(function () { MatDialog.alert(W.saveFailed); });
    });
})();
