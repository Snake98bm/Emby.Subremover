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
            if (field.VisibleIf && field.VisibleIf.Is === 'IsTrue') node.classList.add('sr-indent');
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
            return h('div', {}, [
                count,
                h('div', { class: 'sr-buttons' }, [
                    h('button', { type: 'button', class: 'sr-btn', text: '↺ Restore originals from backups…', onclick: listBackups }),
                    h('span', { class: 'sr-muted', text: 'Lists the videos with a backup to choose from. Nothing is restored until you confirm.' })
                ]),
                status,
                list
            ]);
        }

        function listBackups() {
            var status = el('srRestoreStatus');
            var list = el('srRestoreList');
            list.innerHTML = '';
            showStatus(status, { Text: 'Looking for backups…', State: 'InProgress' });
            getJson('SubRemover/Backups').then(function (r) {
                backupList = r;
                if (r.Refusal) { showStatus(status, { Caption: 'Restore', Text: r.Refusal, State: 'Warning' }); return; }
                showStatus(status, { Caption: 'Restore', State: 'Warning', Text:
                    'Tick the videos to restore, then click Restore selected. Each one is replaced by the original from its backup, ' +
                    'undoing everything SubRemover changed in it. Subtitle files SubRemover saved next to them stay.' +
                    (r.Total > r.Items.length ? ' Only the newest ' + r.Items.length + ' of ' + r.Total + ' are listed; restore older ones in another go.' : '') });

                var boxes = [];
                var restoreBtn = h('button', { type: 'button', class: 'sr-btn sr-danger', text: 'Restore selected', disabled: true, onclick: restoreSelected });
                function updateButton() {
                    var n = boxes.filter(function (b) { return b.checked; }).length;
                    restoreBtn.disabled = n === 0;
                    restoreBtn.textContent = n === 0 ? 'Restore selected' : n === 1 ? 'Restore this video' : 'Restore ' + n + ' videos';
                    all.checked = n > 0 && n === boxes.length;
                }
                var all = h('input', { type: 'checkbox', title: 'Select all', onchange: function () {
                    boxes.forEach(function (b) { b.checked = all.checked; });
                    updateButton();
                } });
                var table = h('table', { class: 'sr-table' }, [h('tr', {}, [h('th', {}, [all]), h('th', { text: 'Video' }), h('th', { text: 'Backup made' })])]);
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
                    var what = picked.length === 1 ? 'this video' : 'these ' + picked.length + ' videos';
                    if (!window.confirm('Restore ' + what + '? Each one is replaced by its original from the backup, undoing what SubRemover changed.')) return;
                    restoreBtn.disabled = true;
                    postJson('SubRemover/Restore', { Key: backupList.Key, Indexes: picked.join(',') }).then(function (s) {
                        showStatus(status, s);
                        list.innerHTML = '';
                    }, function (err) {
                        errorText(err).then(function (t) { showStatus(status, { Caption: 'Restore', Text: 'Restore failed: ' + t, State: 'Failed' }); });
                        restoreBtn.disabled = false;
                    });
                }

                list.appendChild(h('div', { class: 'sr-card' }, [table, h('div', { class: 'sr-buttons' }, [restoreBtn,
                    h('button', { type: 'button', class: 'sr-btn', text: 'Cancel', onclick: function () { list.innerHTML = ''; showStatus(status, null); } })])]));
            }, function (err) {
                errorText(err).then(function (t) { showStatus(status, { Caption: 'Restore', Text: 'Couldn\'t list the backups: ' + t, State: 'Failed' }); });
            });
        }

        function logsWidget() {
            var select = h('select', { id: 'srLogPick', onchange: function () { loadLog(select.value); } });
            (page.Logs || []).forEach(function (n) { select.appendChild(h('option', { value: n, text: n })); });
            if (!(page.Logs || []).length) select.appendChild(h('option', { value: '', text: 'No runs yet' }));
            return h('div', {}, [
                h('div', { class: 'sr-field' }, [
                    h('label', { for: 'srLogPick', text: 'Logs' }),
                    select,
                    h('div', { class: 'sr-help', text: 'Pick an older run to see its log.' })
                ]),
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
            getJson('SubRemover/LogView', { Name: name || select.value || '' }).then(function (r) {
                el('srLogText').value = r.Text || '';
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

        var WIDGETS = { run: runWidget, quickSetup: quickSetupWidget, restore: restoreWidget, logs: logsWidget };

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
            if (!status || status.State !== 'InProgress') return;
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

        view.addEventListener('viewshow', function () { load(); });
        view.addEventListener('viewhide', function () { clearTimeout(pollTimer); });
    };
});
