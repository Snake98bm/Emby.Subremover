define([], function () {
    'use strict';

    // SubRemover's settings page. The server sends the tabs, each setting's label, description, limits and when it shows
    // (GET SubRemover/Page), so this script only draws them; saving sends every setting back as text (POST SubRemover/Settings).

    function api() { return globalThis.ApiClient || window.ApiClient; }

    function getJson(path, query) { return api().getJSON(api().getUrl(path, query)); }

    function postJson(path, body) {
        return api().ajax({ type: 'POST', url: api().getUrl(path), data: JSON.stringify(body || {}), contentType: 'application/json', dataType: 'json' });
    }

    // Emby rejects with a fetch Response, an Error or a string; turn any of them into a message.
    function errorText(err) {
        if (err && typeof err.text === 'function') {
            return err.text().then(function (t) { return t || (err.status + ' ' + (err.statusText || '')); }, function () { return String(err.status || err); });
        }
        return Promise.resolve(err && err.message ? err.message : String(err));
    }

    function h(tag, attrs, children) {
        var e = document.createElement(tag);
        if (attrs) Object.keys(attrs).forEach(function (k) {
            var v = attrs[k];
            if (v == null || v === false) return;
            if (k === 'text') e.textContent = v;
            else if (k === 'class') e.className = v;
            else if (k === 'value') e.value = v;
            else if (k === 'checked') e.checked = !!v;
            else if (k.slice(0, 2) === 'on') e.addEventListener(k.slice(2), v);
            else e.setAttribute(k, v === true ? '' : v);
        });
        (children || []).forEach(function (c) { if (c != null) e.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
        return e;
    }

    function splitList(text) {
        return (text || '').split(',').map(function (s) { return s.trim(); }).filter(function (s) { return s; });
    }

    var STATE_CLASS = { Succeeded: 'ok', Failed: 'err', Warning: 'warn', InProgress: 'busy', Unavailable: 'warn', Unknown: 'warn' };

    return function (view) {
        var page = null;        // what GET SubRemover/Page returned
        var values = {};        // setting name -> value text, as it will be saved
        var fields = {};        // setting name -> FieldDto
        var rows = {};          // setting name -> the element to show or hide
        var redraw = {};        // setting name -> function that refreshes its control from values
        var loaded = false;
        var dirty = false;
        var pollTimer = null;
        // Set while the page is hidden: no status polling then, and a reply still on its way doesn't start it again.
        var hidden = false;
        var backupList = null;  // what GET SubRemover/Backups returned, while the restore list is open

        function el(id) { return view.querySelector('#' + id); }

        function setSaveStatus(text) {
            el('srSaveStatus').textContent = text || '';
            el('srSaveStatusTop').textContent = text || '';
        }

        function markDirty() {
            dirty = true;
            setSaveStatus('Unsaved changes — click Save.');
        }

        function setValue(name, value) {
            values[name] = value;
            markDirty();
            applyConditions();
            if (name === 'KeepLanguages' && redraw.DefaultLanguage) redraw.DefaultLanguage();
        }

        function showStatus(box, status) {
            box.className = 'sr-status ' + (STATE_CLASS[status && status.State] || '');
            box.textContent = '';
            if (!status || !status.Text) return;
            if (status.Caption) box.appendChild(h('b', { text: status.Caption }));
            box.appendChild(document.createTextNode(status.Text));
        }

        // ---------------------------------------------------------------- tabs

        function showTab(id) {
            view.querySelectorAll('.sr-tab').forEach(function (b) { b.setAttribute('aria-selected', b.getAttribute('data-tab') === id ? 'true' : 'false'); });
            view.querySelectorAll('.sr-panel').forEach(function (p) { p.classList.toggle('sr-active', p.getAttribute('data-panel') === id); });
            try { sessionStorage.setItem('subRemoverTab', id); } catch (e) { }
            if (id === 'Logs') loadLog(null);
            if (id === 'Backups') loadReasons();
            if (id === 'Errors') loadErrors();
        }

        // ---------------------------------------------------------------- settings

        function conditionMet(c) {
            if (!c) return true;
            var v = values[c.Field];
            switch (c.Is) {
                case 'IsTrue': return v === 'true';
                case 'IsFalse': return v !== 'true';
                case 'IsNotNullOrEmpty': return !!v;
                case 'IsNull': return !v;
                case 'IsEqual': return v === c.Value;
                case 'IsNotEqual': return v !== c.Value;
                default: return true;
            }
        }

        function applyConditions() {
            Object.keys(rows).forEach(function (name) {
                rows[name].style.display = conditionMet(fields[name].VisibleIf) ? '' : 'none';
            });
        }

        function choicesFor(field) {
            if (field.Options) return field.Options;
            if (field.Source === 'Libraries') return page.Libraries || [];
            if (field.Source === 'Languages') return page.Languages || [];
            if (field.Source === 'DefaultLanguage') {
                // Only the kept languages: the default can't go to a subtitle that gets removed.
                var names = {};
                (page.Languages || []).forEach(function (l) { names[l.Value] = l.Name; });
                var kept = splitList(values.KeepLanguages).filter(function (c) { return c !== 'und'; })
                    .map(function (c) { return { Value: c, Name: names[c] || c }; })
                    .sort(function (a, b) { return a.Name.localeCompare(b.Name); });
                return [{ Value: '', Name: 'Automatic' }].concat(kept);
            }
            return [];
        }

        function description(field) {
            return field.Description ? h('div', { class: 'sr-help', text: field.Description }) : null;
        }

        function boolControl(field) {
            var box = h('input', { type: 'checkbox', checked: values[field.Name] === 'true',
                onchange: function () { setValue(field.Name, box.checked ? 'true' : 'false'); } });
            redraw[field.Name] = function () { box.checked = values[field.Name] === 'true'; };
            return h('label', { class: 'sr-check' }, [box, h('span', {}, [
                h('strong', { text: field.Label }),
                field.Description ? h('br') : null,
                field.Description ? h('span', { class: 'sr-muted', text: field.Description }) : null
            ])]);
        }

        function selectControl(field) {
            var select = h('select', { id: 'sr_' + field.Name, onchange: function () { setValue(field.Name, select.value); } });
            function fill() {
                var current = values[field.Name] || '';
                select.innerHTML = '';
                var choices = choicesFor(field);
                if (field.Source === 'DefaultLanguage' && choices.every(function (c) { return c.Value !== current; })) {
                    values[field.Name] = current = '';
                }
                choices.forEach(function (c) { select.appendChild(h('option', { value: c.Value, text: c.Name })); });
                select.value = current;
            }
            fill();
            redraw[field.Name] = fill;
            return h('div', { class: 'sr-field' }, [h('label', { for: 'sr_' + field.Name, text: field.Label }), select, description(field)]);
        }

        function multiSelectControl(field) {
            var choices = choicesFor(field);
            var long = choices.length > 20;
            var filter = long ? h('input', { type: 'text', class: 'sr-filter', placeholder: 'Type to find…', oninput: function () { draw(); } }) : null;
            var chips = h('div', { class: 'sr-chips' });
            var picked = h('div', { class: 'sr-picked sr-muted' });
            var names = {};
            choices.forEach(function (c) { names[c.Value] = c.Name; });

            function draw() {
                var selected = splitList(values[field.Name]);
                var q = filter ? filter.value.trim().toLowerCase() : '';
                chips.innerHTML = '';
                // Picked ones first, so they're easy to see in a long list.
                choices.slice().sort(function (a, b) {
                    return (selected.indexOf(b.Value) >= 0) - (selected.indexOf(a.Value) >= 0);
                }).forEach(function (c) {
                    var on = selected.indexOf(c.Value) >= 0;
                    if (q && !on && c.Name.toLowerCase().indexOf(q) < 0 && c.Value.toLowerCase().indexOf(q) < 0) return;
                    var box = h('input', { type: 'checkbox', checked: on, onchange: function () {
                        var now = splitList(values[field.Name]).filter(function (v) { return v !== c.Value; });
                        if (box.checked) now.push(c.Value);
                        setValue(field.Name, now.join(','));
                        draw();
                    } });
                    chips.appendChild(h('label', { class: 'sr-chip' + (on ? ' on' : '') }, [box, c.Name]));
                });
                picked.textContent = selected.length
                    ? 'Picked: ' + selected.map(function (v) { return names[v] || v; }).join(', ')
                    : 'Nothing picked.';
            }
            draw();
            redraw[field.Name] = draw;
            return h('div', { class: 'sr-field' }, [h('span', { class: 'sr-label', text: field.Label }), filter, chips, picked, description(field)]);
        }

        function textControl(field) {
            var input;
            if (field.Kind === 'multiline') {
                input = h('textarea', { id: 'sr_' + field.Name, rows: field.Lines || 6 });
            } else {
                input = h('input', { id: 'sr_' + field.Name, type: field.Kind === 'int' ? 'number' : 'text', min: field.Min, max: field.Max, step: field.Kind === 'int' ? 1 : null });
            }
            input.value = values[field.Name] || '';
            input.addEventListener('input', function () { setValue(field.Name, input.value); });
            redraw[field.Name] = function () { input.value = values[field.Name] || ''; };

            var control = input;
            if (field.Kind === 'folder') {
                var browse = h('button', { type: 'button', class: 'sr-btn', text: 'Browse…', onclick: function () {
                    require(['directorybrowser'], function (DirectoryBrowser) {
                        var picker = new DirectoryBrowser();
                        picker.show({
                            header: field.Label,
                            path: input.value,
                            includeFiles: false,
                            callback: function (path) {
                                if (path) { input.value = path; setValue(field.Name, path); }
                                picker.close();
                            }
                        });
                    });
                } });
                control = h('div', { class: 'sr-row' }, [input, browse]);
            }
            return h('div', { class: 'sr-field' }, [h('label', { for: 'sr_' + field.Name, text: field.Label }), control, description(field)]);
        }

        function fieldControl(field) {
            fields[field.Name] = field;
            var node = field.Kind === 'bool' ? boolControl(field)
                : field.Kind === 'multiselect' ? multiSelectControl(field)
                : field.Kind === 'select' ? selectControl(field)
                : textControl(field);
            // Settings that only show with another one on are set in a little.
            if (field.VisibleIf && (field.VisibleIf.Is === 'IsTrue' || field.VisibleIf.Is === 'IsEqual')) node.classList.add('sr-indent');
            rows[field.Name] = node;
            return node;
        }

        // ---------------------------------------------------------------- the page's own parts

        function runWidget() {
            var box = h('div', { class: 'sr-status', id: 'srRunStatus', role: 'status' });
            showStatus(box, page.Status);
            return h('div', {}, [
                h('div', { class: 'sr-buttons' }, [
                    h('button', { type: 'button', class: 'sr-btn sr-primary', text: '▶ Run now', onclick: runNow }),
                    h('span', { class: 'sr-muted', text: 'Saves your settings, then starts a run. Scheduled runs: Scheduled Tasks → SubRemover.' })
                ]),
                box
            ]);
        }

        function hashWidget() {
            var box = h('div', { class: 'sr-status', id: 'srHashStatus', role: 'status' });
            showStatus(box, page.HashStatus);
            function start(countOnly) {
                save().then(function (ok) {
                    if (!ok) return;
                    var over = el('srHashStartOver');
                    postJson('SubRemover/HashSubtitles', { CountOnly: countOnly, StartOver: !!(over && over.checked) }).then(function (s) {
                        if (over) over.checked = false;
                        showStatus(el('srHashStatus'), s);
                        pollHash({ State: 'InProgress' });
                    }, function (err) {
                        errorText(err).then(function (t) { showStatus(el('srHashStatus'), { Caption: 'Hash-matched subtitles', Text: 'Could not start: ' + t, State: 'Failed' }); });
                    });
                });
            }
            return h('div', {}, [
                h('div', { class: 'sr-buttons' }, [
                    h('button', { type: 'button', class: 'sr-btn', text: '🔍 Count hash-matched subtitles', onclick: function () { start(true); } }),
                    h('button', { type: 'button', class: 'sr-btn', text: '⬇ Download hash-matched subtitles', onclick: function () { start(false); } })
                ]),
                h('label', { class: 'sr-check' }, [h('input', { type: 'checkbox', id: 'srHashStartOver' }),
                    h('span', { text: 'Start over (otherwise a stopped Count or Download carries on where it got to)' })]),
                h('div', { class: 'sr-muted', text: 'Goes through your libraries without changing any video. Count only asks the downloaders and downloads nothing. Run Download before SubRemover or AudioRemover change your videos.' }),
                box
            ]);
        }

        var hashTimer;
        function pollHash(status) {
            clearTimeout(hashTimer);
            if (hidden || !status || status.State !== 'InProgress') return;
            hashTimer = setTimeout(function () {
                getJson('SubRemover/HashSubtitles/Status').then(function (s) {
                    var box = el('srHashStatus');
                    if (box) showStatus(box, s);
                    pollHash(s);
                }, function () { pollHash(status); });
            }, 4000);
        }

        // "Ads in subtitle files": Find / Remove now, and the suggestions the last one found for "Text to remove".
        function adsWidget() {
            var box = h('div', { class: 'sr-status', id: 'srAdStatus', role: 'status' });
            var list = h('div', { id: 'srAdSuggestions' });
            var logLink = h('a', { class: 'sr-btn', id: 'srAdLog', target: '_blank', rel: 'noopener', text: '📄 Open the log ↗', style: 'display:none' });
            function start(findOnly) {
                if (!findOnly && !window.confirm('Remove the ads from every .srt file next to the videos in your libraries? Each changed file is backed up first.')) return;
                save().then(function (ok) {
                    if (!ok) return;
                    postJson('SubRemover/SubtitleAds', { FindOnly: findOnly }).then(function (r) {
                        showAds(r);
                        pollAds({ State: 'InProgress' });
                    }, function (err) {
                        errorText(err).then(function (t) { showStatus(el('srAdStatus'), { Caption: 'Ads in subtitle files', Text: 'Could not start: ' + t, State: 'Failed' }); });
                    });
                });
            }
            setTimeout(function () { showAds(page.AdStatus); }, 0);
            return h('div', {}, [
                h('div', { class: 'sr-buttons' }, [
                    h('button', { type: 'button', class: 'sr-btn', text: '🧪 Test run (changes nothing)', onclick: function () { start(true); } }),
                    h('button', { type: 'button', class: 'sr-btn', text: '🧹 Remove ads now', onclick: function () { start(false); } }),
                    logLink
                ]),
                h('div', { class: 'sr-muted', text: 'Every .srt file next to the videos in your libraries. Test run only lists what would go, file by file and line by line, in its log; Remove takes it out and backs each file up. No video is changed.' }),
                box,
                list
            ]);
        }

        function showAds(r) {
            var box = el('srAdStatus');
            var list = el('srAdSuggestions');
            if (!box || !r) return;
            showStatus(box, r.Status);
            var logLink = el('srAdLog');
            if (logLink) {
                logLink.style.display = r.LogUrl ? '' : 'none';
                if (r.LogUrl) logLink.href = r.LogUrl;
            }
            list.innerHTML = '';
            var items = (r.Suggestions || []);
            var current = (values.SubtitleAdText || '').toLowerCase().split(/\r?\n/).map(function (l) { return l.trim(); });
            items = items.filter(function (s) { return current.indexOf(s.Entry.toLowerCase()) < 0; });
            if (!items.length) return;
            var boxes = [];
            var table = h('table', { class: 'sr-table' }, [h('tr', {}, [h('th'), h('th', { text: 'Suggested text' }), h('th', { text: 'Found in' })])]);
            items.forEach(function (s) {
                var b = h('input', { type: 'checkbox' });
                b._entry = s.Entry;
                boxes.push(b);
                table.appendChild(h('tr', {}, [
                    h('td', {}, [b]),
                    h('td', {}, [h('div', { class: 'sr-file', text: s.Entry }), h('div', { class: 'sr-folder', text: 'e.g. "' + s.Example + '"' })]),
                    h('td', {}, [h('div', { text: s.Titles + ' films or shows' }), h('div', { class: 'sr-folder', text: s.TitleNames })])
                ]));
            });
            var add = h('button', { type: 'button', class: 'sr-btn', text: 'Add ticked to the list', onclick: function () {
                var picked = boxes.filter(function (b) { return b.checked; }).map(function (b) { return b._entry; });
                if (!picked.length) return;
                var text = (values.SubtitleAdText || '').replace(/\s+$/, '');
                setValue('SubtitleAdText', (text ? text + '\n' : '') + picked.join('\n'));
                if (redraw.SubtitleAdText) redraw.SubtitleAdText();
                showAds(r);
            } });
            list.appendChild(h('div', { class: 'sr-card' }, [
                h('div', { class: 'sr-help', text: 'Found in your subtitle files, not on the list yet. Tick the ones that are ads, add them, then click Save.' }),
                table,
                h('div', { class: 'sr-buttons' }, [add])
            ]));
        }

        var adTimer;
        function pollAds(status) {
            clearTimeout(adTimer);
            if (hidden) return;
            if (!status || status.State !== 'InProgress') return;
            adTimer = setTimeout(function () {
                getJson('SubRemover/SubtitleAds/Status').then(function (r) {
                    showAds(r);
                    pollAds(r.Status);
                }, function () { pollAds(status); });
            }, 4000);
        }

        // "Timing of subtitle files": Scan run / Fix now.
        function timingWidget() {
            var box = h('div', { class: 'sr-status', id: 'srTimingStatus', role: 'status' });
            var logLink = h('a', { class: 'sr-btn', id: 'srTimingLog', target: '_blank', rel: 'noopener', text: '📄 Open the log ↗', style: 'display:none' });
            function start(testOnly) {
                if (!testOnly && !window.confirm('Fix the timing of every .srt file next to the videos in your libraries? Files that are off get moved, and out-of-sync copies of a language that has an in-sync one get deleted. Each one is backed up first.')) return;
                save().then(function (ok) {
                    if (!ok) return;
                    var over = el('srTimingStartOver');
                    postJson('SubRemover/SubtitleTiming', { TestOnly: testOnly, StartOver: !!(over && over.checked) }).then(function (r) {
                        if (over) over.checked = false;
                        showTiming(r);
                        pollTiming({ State: 'InProgress' });
                    }, function (err) {
                        errorText(err).then(function (t) { showStatus(el('srTimingStatus'), { Caption: 'Subtitle file timing', Text: 'Could not start: ' + t, State: 'Failed' }); });
                    });
                });
            }
            setTimeout(function () { showTiming(page.TimingStatus); }, 0);
            return h('div', {}, [
                h('div', { class: 'sr-buttons' }, [
                    h('button', { type: 'button', class: 'sr-btn', text: '🔍 Scan run (changes nothing)', onclick: function () { start(true); } }),
                    h('button', { type: 'button', class: 'sr-btn', text: '⏱ Fix timing now', onclick: function () { start(false); } }),
                    logLink
                ]),
                h('label', { class: 'sr-check' }, [h('input', { type: 'checkbox', id: 'srTimingStartOver' }),
                    h('span', { text: 'Start over: measure every file again, even ones found good before (otherwise a stopped run carries on, and Fix skips files already checked)' })]),
                h('div', { class: 'sr-muted', text: 'Every .srt file next to the videos in your libraries, measured against the speech in the video. Only moves whole files, never stretches them. Scan run lists each file and how far off it is in its log, and saves that as a plan: Fix timing now then uses it straight away and only measures again the videos whose files changed since. Reading a video\'s audio is the slow part. No video is changed.' }),
                box
            ]);
        }

        function showTiming(r) {
            var box = el('srTimingStatus');
            if (!box || !r) return;
            showStatus(box, r.Status);
            var logLink = el('srTimingLog');
            if (logLink) {
                logLink.style.display = r.LogUrl ? '' : 'none';
                if (r.LogUrl) logLink.href = r.LogUrl;
            }
        }

        var timingTimer;
        function pollTiming(status) {
            clearTimeout(timingTimer);
            if (hidden) return;
            if (!status || status.State !== 'InProgress') return;
            timingTimer = setTimeout(function () {
                getJson('SubRemover/SubtitleTiming/Status').then(function (r) {
                    showTiming(r);
                    pollTiming(r.Status);
                }, function () { pollTiming(status); });
            }, 4000);
        }

        function quickSetupWidget() {
            return h('div', { class: 'sr-buttons' }, [
                h('button', { type: 'button', class: 'sr-btn', text: 'Quick setup: English + no language', onclick: function () {
                    values.KeepLanguages = 'eng';
                    values.KeepUndefined = 'true';
                    Object.keys(redraw).forEach(function (k) { redraw[k](); });
                    applyConditions();
                    save();
                } }),
                h('span', { class: 'sr-muted', text: 'Keeps English and subtitles with no language, and saves.' })
            ]);
        }

        function restoreWidget() {
            var count = h('p', { class: 'sr-help', id: 'srBackupCount', text: page.BackupCount || '' });
            var status = h('div', { class: 'sr-status', id: 'srRestoreStatus', role: 'status' });
            var list = h('div', { id: 'srRestoreList' });
            var show = h('select', { id: 'srRestoreShow', title: 'Which backups to list', onchange: function () {
                // The list is open: show it again with the new choice; otherwise just fill in that kind's reasons.
                if (el('srRestoreList').childNodes.length) listBackups(); else loadReasons();
            } }, [
                h('option', { value: 'all', text: 'All backups' }),
                h('option', { value: 'videos', text: 'Video files only' }),
                h('option', { value: 'subtitles', text: 'Subtitle files (.srt) only' })
            ]);
            // Filled with the reasons (and counts) the server finds once the list is shown.
            var why = h('select', { id: 'srRestoreWhy', title: 'Why the backup was made', onchange: function () {
                if (el('srRestoreList').childNodes.length) listBackups();
            } }, [h('option', { value: 'any', text: 'Any reason' })]);
            return h('div', {}, [
                count,
                h('div', { class: 'sr-buttons' }, [
                    h('label', { class: 'sr-muted', text: 'Show' }), show,
                    h('label', { class: 'sr-muted', text: 'Why' }), why,
                    h('button', { type: 'button', class: 'sr-btn', text: '↺ Restore originals from backups…', onclick: listBackups }),
                    h('span', { class: 'sr-muted', text: 'Lists the files with a backup to choose from. Nothing is restored until you confirm.' })
                ]),
                status,
                list
            ]);
        }

        // Fills the Why list with the reasons (and counts) for the chosen kind, without listing the backups.
        function loadReasons() {
            var whySelect = el('srRestoreWhy');
            var showSelect = el('srRestoreShow');
            if (!whySelect || !showSelect) return;
            getJson('SubRemover/Backups', { Show: showSelect.value, Why: 'any' }).then(function (r) { fillReasons(whySelect, r.Reasons); }, function () { });
        }

        function fillReasons(whySelect, reasons) {
            var keep = whySelect.value || 'any';
            whySelect.innerHTML = '';
            whySelect.appendChild(h('option', { value: 'any', text: 'Any reason' }));
            (reasons || []).forEach(function (x) { whySelect.appendChild(h('option', { value: x.Value, text: x.Label + ' (' + x.Count + ')' })); });
            whySelect.value = [].some.call(whySelect.options, function (o) { return o.value === keep; }) ? keep : 'any';
        }

        function listBackups() {
            var status = el('srRestoreStatus');
            var list = el('srRestoreList');
            var show = el('srRestoreShow').value;
            var whySelect = el('srRestoreWhy');
            var why = whySelect.value || 'any';
            // what one row is, for the button and confirm texts
            var noun = show === 'videos' ? ['video', 'videos'] : show === 'subtitles' ? ['subtitle file', 'subtitle files'] : ['file', 'files'];
            list.innerHTML = '';
            showStatus(status, { Text: 'Looking for backups…', State: 'InProgress' });
            getJson('SubRemover/Backups', { Show: show, Why: why }).then(function (r) {
                backupList = r;
                // The reasons for this kind of backup, with counts; a reason that's gone falls back to "Any reason".
                whySelect.innerHTML = '';
                whySelect.appendChild(h('option', { value: 'any', text: 'Any reason' }));
                (r.Reasons || []).forEach(function (x) { whySelect.appendChild(h('option', { value: x.Value, text: x.Label + ' (' + x.Count + ')' })); });
                whySelect.value = [].some.call(whySelect.options, function (o) { return o.value === why; }) ? why : 'any';
                if (r.Refusal) { showStatus(status, { Caption: 'Restore', Text: r.Refusal, State: 'Warning' }); return; }
                showStatus(status, { Caption: 'Restore', State: 'Warning', Text:
                    'Tick the ' + noun[1] + ' to restore, then click Restore selected. Each one is replaced by the original from its backup, ' +
                    'undoing everything SubRemover changed in it.' +
                    (show === 'subtitles' ? '' : ' Subtitle files SubRemover saved next to restored videos stay.') +
                    (r.Total > r.Items.length ? ' Only the newest ' + r.Items.length + ' of ' + r.Total + ' are listed; restore older ones in another go.' : '') });

                var boxes = [];
                var restoreBtn = h('button', { type: 'button', class: 'sr-btn sr-danger', text: 'Restore selected', disabled: true, onclick: restoreSelected });
                var deleteBtn = h('button', { type: 'button', class: 'sr-btn', text: '🗑 Delete selected backups', disabled: true, onclick: deleteSelected });
                function updateButton() {
                    var n = boxes.filter(function (b) { return b.checked; }).length;
                    restoreBtn.disabled = n === 0;
                    deleteBtn.disabled = n === 0;
                    restoreBtn.textContent = n === 0 ? 'Restore selected' : n === 1 ? 'Restore this ' + noun[0] : 'Restore ' + n + ' ' + noun[1];
                    deleteBtn.textContent = n === 0 ? '🗑 Delete selected backups' : n === 1 ? '🗑 Delete this backup' : '🗑 Delete ' + n + ' backups';
                    all.checked = n > 0 && n === boxes.length;
                }
                var all = h('input', { type: 'checkbox', title: 'Select all', onchange: function () {
                    boxes.forEach(function (b) { b.checked = all.checked; });
                    updateButton();
                } });
                var table = h('table', { class: 'sr-table' }, [h('tr', {}, [h('th', {}, [all]), h('th', { text: show === 'videos' ? 'Video' : show === 'subtitles' ? 'Subtitle file' : 'File' }), h('th', { text: 'Backup made' })])]);
                r.Items.forEach(function (item, i) {
                    var box = h('input', { type: 'checkbox', 'data-index': String(i), onchange: updateButton });
                    boxes.push(box);
                    table.appendChild(h('tr', {}, [
                        h('td', {}, [box]),
                        h('td', {}, [h('div', { class: 'sr-file', text: item.File }), h('div', { class: 'sr-folder', text: item.Folder }),
                            item.Missing ? h('div', { class: 'sr-folder', text: 'Backup missing — will be skipped.' }) : null]
                            .concat((item.Changes || []).map(function (line) { return h('div', { class: 'sr-change', text: line }); }))
                            .concat(item.ChangesNote ? [h('div', { class: 'sr-change sr-unrecorded', text: item.ChangesNote })] : [])),
                        h('td', { text: item.Made })
                    ]));
                });

                function restoreSelected() {
                    var picked = boxes.filter(function (b) { return b.checked; }).map(function (b) { return b.getAttribute('data-index'); });
                    if (!picked.length) return;
                    var what = picked.length === 1 ? 'this ' + noun[0] : 'these ' + picked.length + ' ' + noun[1];
                    if (!window.confirm('Restore ' + what + '? Each one is replaced by its original from the backup, undoing what SubRemover changed.')) return;
                    restoreBtn.disabled = true;
                    postJson('SubRemover/Restore', { Key: backupList.Key, Indexes: picked.join(','), Show: show, Why: why }).then(function (s) {
                        showStatus(status, s);
                        list.innerHTML = '';
                    }, function (err) {
                        errorText(err).then(function (t) { showStatus(status, { Caption: 'Restore', Text: 'Restore failed: ' + t, State: 'Failed' }); });
                        restoreBtn.disabled = false;
                    });
                }

                // Deleting a backup can't be undone, so the word has to be typed (any capitals); the server checks it too.
                function deleteSelected() {
                    var picked = boxes.filter(function (b) { return b.checked; }).map(function (b) { return b.getAttribute('data-index'); });
                    if (!picked.length) return;
                    var what = picked.length === 1 ? 'this backup' : 'these ' + picked.length + ' backups';
                    var typed = window.prompt('Delete ' + what + ' for good? The ' + (picked.length === 1 ? noun[0] : noun[1]) +
                        ' stay as they are now, but can never be restored. Type Delete to confirm.', '');
                    if (typed === null) return;
                    if (typed.trim().toLowerCase() !== 'delete') {
                        showStatus(status, { Caption: 'Restore', Text: 'Nothing deleted: type Delete to confirm.', State: 'Warning' });
                        return;
                    }
                    deleteBtn.disabled = true;
                    postJson('SubRemover/DeleteBackups', { Key: backupList.Key, Indexes: picked.join(','), Show: show, Why: why, Confirm: typed }).then(function (s) {
                        showStatus(status, s);
                        list.innerHTML = '';
                        getJson('SubRemover/Page').then(function (p) { var c = el('srBackupCount'); if (c && p.BackupCount) c.textContent = p.BackupCount; });
                    }, function (err) {
                        errorText(err).then(function (t) { showStatus(status, { Caption: 'Restore', Text: 'Delete failed: ' + t, State: 'Failed' }); });
                        deleteBtn.disabled = false;
                    });
                }

                // More than 10 rows: the table scrolls in a box 10 rows high, so the buttons stay in reach.
                var scroller = h('div', { class: 'sr-scroll' }, [table]);
                list.appendChild(h('div', { class: 'sr-card' }, [scroller, h('div', { class: 'sr-buttons' }, [restoreBtn, deleteBtn,
                    h('button', { type: 'button', class: 'sr-btn', text: 'Cancel', onclick: function () { list.innerHTML = ''; showStatus(status, null); } })])]));
                if (r.Items.length > 10) {
                    var tenth = table.rows[10];   // rows[0] is the header
                    scroller.style.maxHeight = (tenth.offsetTop + tenth.offsetHeight + 2) + 'px';
                    scroller.style.overflowY = 'auto';
                }
            }, function (err) {
                errorText(err).then(function (t) { showStatus(status, { Caption: 'Restore', Text: 'Couldn\'t list the backups: ' + t, State: 'Failed' }); });
            });
        }

        // The Logs tab's filter: value sent to the server, and its label (the count is added after each load).
        var LOG_FILTERS = [
            ['all', 'Everything'],
            ['Videos', 'Videos (tracks removed or changed)'],
            ['SubtitleFiles', 'Subtitle files (.srt: ads, timing, extracted)'],
            ['HashDownloads', 'Hash-matched subtitle downloads'],
            ['Ocr', 'OCR (picture subtitles to text)'],
            ['problems', 'Problems (warnings and errors)']
        ];

        function logsWidget() {
            var select = h('select', { id: 'srLogPick', onchange: function () { loadLog(select.value); } });
            (page.Logs || []).forEach(function (n) { select.appendChild(h('option', { value: n, text: n })); });
            if (!(page.Logs || []).length) select.appendChild(h('option', { value: '', text: 'No runs yet' }));
            var show = h('select', { id: 'srLogShow', onchange: function () { loadLog(select.value); } });
            LOG_FILTERS.forEach(function (f) { show.appendChild(h('option', { value: f[0], text: f[1] })); });
            var details = h('input', { type: 'checkbox', id: 'srLogHideDetails', onchange: function () { loadLog(select.value); } });
            return h('div', {}, [
                h('div', { class: 'sr-field' }, [
                    h('label', { for: 'srLogPick', text: 'Logs' }),
                    select,
                    h('div', { class: 'sr-help', text: 'Pick an older run to see its log.' })
                ]),
                h('div', { class: 'sr-field' }, [
                    h('label', { for: 'srLogShow', text: 'Show' }),
                    show,
                    h('div', { class: 'sr-help', text: 'Only the lines about videos, subtitle files, downloads, OCR or problems. Open in a new window and Download always give the whole log.' })
                ]),
                h('label', { class: 'sr-check' }, [details, h('span', { text: 'Hide details (Debug lines: every video looked at, even when nothing was done)' })]),
                h('div', { class: 'sr-buttons' }, [
                    h('a', { class: 'sr-btn', id: 'srLogOpen', target: '_blank', rel: 'noopener', text: 'Open in a new window ↗' }),
                    h('a', { class: 'sr-btn', id: 'srLogDownload', text: '⬇ Download' }),
                    h('button', { type: 'button', class: 'sr-btn', text: '⟳ Refresh', onclick: function () { loadLog(select.value); } })
                ]),
                h('textarea', { id: 'srLogText', rows: 24, readonly: true, spellcheck: 'false' })
            ]);
        }

        function loadLog(name) {
            var select = el('srLogPick');
            if (!select) return;
            // No name (opening the tab): the server sends the newest log, including runs since this page was opened.
            var show = el('srLogShow');
            var hide = el('srLogHideDetails');
            getJson('SubRemover/LogView', { Name: name || '', Show: show ? show.value : 'all', HideDetails: !!(hide && hide.checked) }).then(function (r) {
                el('srLogText').value = r.Text || '';
                if (show && r.Counts) {
                    [].forEach.call(show.options, function (o) {
                        var f = LOG_FILTERS.filter(function (x) { return x[0] === o.value; })[0];
                        var n = r.Counts[o.value];
                        o.textContent = f[1] + (n === undefined ? '' : ' - ' + n);
                    });
                }
                var has = !!r.Name;
                [['srLogOpen', r.OpenUrl], ['srLogDownload', r.DownloadUrl]].forEach(function (x) {
                    var a = el(x[0]);
                    a.style.display = has ? '' : 'none';
                    if (has) a.href = x[1];
                });
                if (has) {
                    if (![].some.call(select.options, function (o) { return o.value === r.Name; })) {
                        select.insertBefore(h('option', { value: r.Name, text: r.Name }), select.firstChild);
                    }
                    select.value = r.Name;
                }
            }, function (err) {
                errorText(err).then(function (t) { el('srLogText').value = 'Couldn\'t read the log: ' + t; });
            });
        }

        // ---------------------------------------------------------------- Errors tab

        function errorsWidget() {
            return h('div', {}, [
                h('h3', { text: 'Videos that failed' }),
                h('div', { class: 'sr-buttons' }, [
                    h('button', { type: 'button', class: 'sr-btn', text: '⟳ Refresh', onclick: loadErrors }),
                    h('span', { class: 'sr-muted', text: 'Videos that failed in recent runs. Tick some to retry them.' })
                ]),
                h('div', { class: 'sr-status', id: 'srErrorsStatus', role: 'status' }),
                h('div', { id: 'srErrorsList' }),
                h('h3', { text: 'What\'s been going on' }),
                h('div', { class: 'sr-help', text: 'Every failure, skip and retry across runs, newest first (subremover-failures.log in Emby\'s log folder). Each line names the run log with the details.' }),
                h('div', { class: 'sr-buttons' }, [
                    h('a', { class: 'sr-btn', id: 'srFailLogOpen', target: '_blank', rel: 'noopener', text: 'Open in a new window ↗' }),
                    h('a', { class: 'sr-btn', id: 'srFailLogDownload', text: '⬇ Download' })
                ]),
                h('textarea', { id: 'srFailLogText', rows: 16, readonly: true, spellcheck: 'false' })
            ]);
        }

        // after: a status to show once the list is drawn again (what Retry said), instead of the list's own summary.
        function loadErrors(after) {
            var list = el('srErrorsList');
            var status = el('srErrorsStatus');
            if (!list) return;
            getJson('SubRemover/Failures').then(function (r) {
                el('srFailLogText').value = r.LogText || 'Nothing yet.';
                el('srFailLogOpen').href = r.LogOpenUrl;
                el('srFailLogDownload').href = r.LogDownloadUrl;
                drawErrors(r);
                if (after && after.Text) showStatus(status, after);
            }, function (err) {
                errorText(err).then(function (t) { showStatus(status, { Caption: 'Errors', Text: 'Couldn\'t list the failed videos: ' + t, State: 'Failed' }); });
            });
        }

        function drawErrors(r) {
            var list = el('srErrorsList');
            var status = el('srErrorsStatus');
            list.innerHTML = '';
            var items = r.Items || [];
            var skipped = items.filter(function (x) { return x.Skipped; }).length;
            if (!items.length) {
                showStatus(status, { Caption: 'Errors', Text: 'No video has failed since it last worked.', State: 'Succeeded' });
                return;
            }
            showStatus(status, { Caption: 'Errors', State: skipped ? 'Warning' : 'None', Text:
                items.length + (items.length === 1 ? ' video has' : ' videos have') + ' failed recently' +
                (r.MaxAttempts > 0 ? '; ' + skipped + ' of them ' + (skipped === 1 ? 'is' : 'are') + ' skipped by runs now (failed ' + r.MaxAttempts + ' runs in a row).'
                    : '. Stop trying is off (0), so every run tries them again.') });

            var boxes = [];
            var retryBtn = h('button', { type: 'button', class: 'sr-btn sr-primary', text: '↻ Retry now', disabled: true, onclick: function () { retry(true); } });
            var laterBtn = h('button', { type: 'button', class: 'sr-btn', text: 'Retry on next run', disabled: true, onclick: function () { retry(false); } });
            var all = h('input', { type: 'checkbox', title: 'Select all', onchange: function () {
                boxes.forEach(function (b) { b.checked = all.checked; });
                update();
            } });
            function update() {
                var n = boxes.filter(function (b) { return b.checked; }).length;
                retryBtn.disabled = laterBtn.disabled = n === 0;
                retryBtn.textContent = n > 1 ? '↻ Retry ' + n + ' now' : '↻ Retry now';
                all.checked = n > 0 && n === boxes.length;
            }
            function retry(now) {
                var picked = boxes.filter(function (b) { return b.checked; }).map(function (b) { return b._path; });
                if (!picked.length) return;
                retryBtn.disabled = laterBtn.disabled = true;
                postJson('SubRemover/Failures/Retry', { Paths: picked.join('\n'), Now: now }).then(function (s) {
                    loadErrors(s);
                }, function (err) {
                    errorText(err).then(function (t) { showStatus(status, { Caption: 'Errors', Text: 'Retry failed: ' + t, State: 'Failed' }); });
                    update();
                });
            }

            var table = h('table', { class: 'sr-table' }, [h('tr', {}, [h('th', {}, [all]), h('th', { text: 'Video' }), h('th', { text: 'Failed' }), h('th', { text: 'Last failed' })])]);
            items.forEach(function (item) {
                var box = h('input', { type: 'checkbox', onchange: update });
                box._path = item.Path;
                boxes.push(box);
                var attempts = h('details', {}, [h('summary', { class: 'sr-folder', text: 'Every attempt (' + item.Attempts.length + ')' })]
                    .concat(item.Attempts.map(function (a) {
                        return h('div', { class: 'sr-change' }, [
                            h('b', { text: a.When + ': ' }), a.Reason || '',
                            a.LogUrl ? ' ' : null,
                            a.LogUrl ? h('a', { href: a.LogUrl, target: '_blank', rel: 'noopener', text: a.LogName + ' ↗' }) : (a.LogName ? ' (' + a.LogName + ', no longer kept)' : null)
                        ]);
                    })));
                table.appendChild(h('tr', {}, [
                    h('td', {}, [box]),
                    h('td', {}, [
                        h('div', { class: 'sr-file', text: item.File }),
                        h('div', { class: 'sr-folder', text: item.Folder }),
                        h('div', { class: 'sr-change', text: item.LastReason || '' }),
                        attempts
                    ]),
                    h('td', {}, [
                        h('div', { text: item.Count + (item.Count === 1 ? ' run' : ' runs') + (r.MaxAttempts > 0 ? ' of ' + r.MaxAttempts : '') }),
                        item.Skipped ? h('div', { class: 'sr-folder', text: 'Skipped by runs now' }) : null
                    ]),
                    h('td', { text: item.LastFailed })
                ]));
            });
            var scroller = h('div', { class: 'sr-scroll' }, [table]);
            list.appendChild(h('div', { class: 'sr-card' }, [scroller, h('div', { class: 'sr-buttons' }, [retryBtn, laterBtn,
                h('span', { class: 'sr-muted', text: 'Retry now runs just these (no limit per run). Retry on next run only forgets their failures.' })])]));
            if (items.length > 10) {
                scroller.style.maxHeight = '40em';
                scroller.style.overflowY = 'auto';
            }
        }

        // ---- Updates: the status line, Check for updates now, and Install now when a newer release is out.

        function showUpdate(u) {
            var box = el('srUpdateStatus');
            if (!box || !u) return;
            showStatus(box, u.Status);
            var install = el('srUpdateInstall');
            install.style.display = u.Available ? '' : 'none';
            install.textContent = '⬇ Install v' + (u.Available || '') + ' now';
            var notes = el('srUpdateNotes');
            notes.style.display = u.ReleaseUrl ? '' : 'none';
            if (u.ReleaseUrl) notes.href = u.ReleaseUrl;
        }

        function startUpdate(install) {
            postJson('SubRemover/Update', { Install: install }).then(function (u) {
                showUpdate(u);
                pollUpdate(u.Status);
            }, function (err) {
                errorText(err).then(function (t) { showStatus(el('srUpdateStatus'), { Caption: 'Updates', Text: 'Could not start: ' + t, State: 'Failed' }); });
            });
        }

        var updateTimer;
        function pollUpdate(status) {
            clearTimeout(updateTimer);
            if (hidden || !status || status.State !== 'InProgress') return;
            updateTimer = setTimeout(function () {
                getJson('SubRemover/Update/Status').then(function (u) {
                    showUpdate(u);
                    pollUpdate(u.Status);
                }, function () { pollUpdate(status); });
            }, 3000);
        }

        function updateWidget() {
            var node = h('div', {}, [
                h('div', { class: 'sr-buttons' }, [
                    h('button', { type: 'button', class: 'sr-btn', text: '🔄 Check for updates now', onclick: function () { startUpdate(false); } }),
                    h('button', { type: 'button', class: 'sr-btn sr-primary', id: 'srUpdateInstall', style: 'display:none', onclick: function () { startUpdate(true); } }),
                    h('a', { class: 'sr-btn', id: 'srUpdateNotes', target: '_blank', rel: 'noopener', style: 'display:none', text: 'Release notes ↗' })
                ]),
                h('div', { class: 'sr-status', id: 'srUpdateStatus', role: 'status' })
            ]);
            // Drawn once it's on the page, since showUpdate finds its parts by id.
            setTimeout(function () { showUpdate(page.UpdateStatus); }, 0);
            return node;
        }

        var WIDGETS = { run: runWidget, quickSetup: quickSetupWidget, restore: restoreWidget, logs: logsWidget, hashSubs: hashWidget, subAds: adsWidget, subTiming: timingWidget, errors: errorsWidget, update: updateWidget };

        // ---------------------------------------------------------------- draw

        function draw() {
            el('srTitle').textContent = page.Title;
            el('srLead').textContent = page.Description;
            var tabs = el('srTabs');
            var panels = el('srPanels');
            tabs.innerHTML = '';
            panels.innerHTML = '';
            fields = {}; rows = {}; redraw = {};

            page.Tabs.forEach(function (tab) {
                tabs.appendChild(h('button', { type: 'button', class: 'sr-tab', role: 'tab', 'data-tab': tab.Id, text: tab.Name,
                    onclick: function () { showTab(tab.Id); } }));
                var panel = h('section', { class: 'sr-panel', 'data-panel': tab.Id });
                tab.Entries.forEach(function (e) {
                    if (e.Kind === 'heading') panel.appendChild(h('h3', { text: e.Text }));
                    else if (e.Kind === 'field') panel.appendChild(fieldControl(e.Field));
                    else if (WIDGETS[e.Text]) panel.appendChild(WIDGETS[e.Text]());
                });
                if (tab.Help && tab.Help.length) {
                    var dl = h('dl');
                    tab.Help.forEach(function (x) { dl.appendChild(h('dt', { text: x.Setting })); dl.appendChild(h('dd', { text: x.Text })); });
                    panel.appendChild(h('div', { class: 'sr-helpbox' }, [h('h3', { text: 'Help' }), dl]));
                }
                panels.appendChild(panel);
            });
            applyConditions();

            var start = 'Run';
            try { start = sessionStorage.getItem('subRemoverTab') || 'Run'; } catch (e) { }
            if (!page.Tabs.some(function (t) { return t.Id === start; })) start = 'Run';
            showTab(start);
        }

        // ---------------------------------------------------------------- load / save / run

        function load() {
            el('srLoadError').textContent = '';
            getJson('SubRemover/Page').then(function (p) {
                page = p;
                values = Object.assign({}, p.Values);
                loaded = true;
                dirty = false;
                draw();
                setSaveStatus('');
                pollIfRunning(p.Status);
                pollHash(p.HashStatus);
                pollAds(p.AdStatus && p.AdStatus.Status);
                pollTiming(p.TimingStatus && p.TimingStatus.Status);
                pollUpdate(p.UpdateStatus && p.UpdateStatus.Status);
            }, function (err) {
                errorText(err).then(function (t) { el('srLoadError').textContent = 'Couldn\'t load the settings: ' + t; });
            });
        }

        function save() {
            if (!loaded) return Promise.resolve(false);
            setSaveStatus('Saving…');
            return postJson('SubRemover/Settings', { Json: JSON.stringify(values) }).then(function (r) {
                if (!r.Ok) {
                    setSaveStatus(r.Message);
                    return false;
                }
                values = Object.assign({}, r.Values);
                Object.keys(redraw).forEach(function (k) { redraw[k](); });
                applyConditions();
                dirty = false;
                setSaveStatus('Saved.');
                if (window.Dashboard && Dashboard.processPluginConfigurationUpdateResult) {
                    try { Dashboard.processPluginConfigurationUpdateResult(); } catch (e) { }
                }
                return true;
            }, function (err) {
                return errorText(err).then(function (t) { setSaveStatus('Not saved: ' + t); return false; });
            });
        }

        function runNow() {
            save().then(function (ok) {
                if (!ok) return;
                postJson('SubRemover/Run').then(function (s) {
                    showStatus(el('srRunStatus'), s);
                    pollIfRunning({ State: 'InProgress' });
                }, function (err) {
                    errorText(err).then(function (t) { showStatus(el('srRunStatus'), { Caption: 'Status', Text: 'Couldn\'t start: ' + t, State: 'Failed' }); });
                });
            });
        }

        // While a run is going, the status line follows it.
        function pollIfRunning(status) {
            clearTimeout(pollTimer);
            if (hidden || !status || status.State !== 'InProgress') return;
            pollTimer = setTimeout(function () {
                getJson('SubRemover/Status').then(function (s) {
                    var box = el('srRunStatus');
                    if (box) showStatus(box, s);
                    pollIfRunning(s);
                }, function () { pollIfRunning(status); });
            }, 4000);
        }

        el('srForm').addEventListener('submit', function (e) { e.preventDefault(); save(); return false; });
        el('srSaveTop').addEventListener('click', function () { save(); });
        el('srRunTop').addEventListener('click', function () { showTab('Run'); runNow(); });

        view.addEventListener('viewshow', function () { hidden = false; load(); });
        view.addEventListener('viewhide', function () {
            hidden = true;
            clearTimeout(pollTimer);
            clearTimeout(hashTimer);
            clearTimeout(adTimer);
            clearTimeout(timingTimer);
            clearTimeout(updateTimer);
        });
    };
});
