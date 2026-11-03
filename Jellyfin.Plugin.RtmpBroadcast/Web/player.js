(() => {
    'use strict';

    const installed = Symbol.for('jellyfin.plugin.twitchBroadcast.player');
    if (window[installed]) return;
    window[installed] = true;
    let panel;

    async function request(api, action, method = 'GET', body) {
        const url = new URL(api.getUrl('TwitchBroadcast/' + action), location.href);
        if (url.origin !== location.origin) throw new Error('Open Jellyfin Web from your server’s own address.');
        const headers = { Accept: 'application/json' };
        api.setRequestHeaders(headers);
        if (body) headers['Content-Type'] = 'application/json';
        const response = await fetch(url, {
            method, headers, body: body && JSON.stringify(body), mode: 'same-origin',
            redirect: 'error', cache: 'no-store', signal: AbortSignal.timeout(10000)
        });
        if (!response.ok) {
            const message = response.status === 409 ? 'The broadcast or playback session changed.'
                : 'RTMP Broadcast returned HTTP ' + response.status + '.';
            throw Object.assign(new Error(message), { status: response.status });
        }
        if (method === 'GET') return response.json();
    }

    function mount(container, api, userId) {
        const button = document.createElement('button');
        button.setAttribute('is', 'paper-icon-button-light');
        button.classList.add('paper-icon-button-light', 'autoSize', 'show-focus', 'jtb-player-control', 'hide');
        button.type = 'button';
        button.disabled = true;
        const icon = document.createElement('span');
        icon.className = 'xlargePaperIconButton material-icons settings_input_antenna';
        icon.setAttribute('aria-hidden', 'true');
        const badge = document.createElement('span');
        badge.style.cssText = 'position:absolute;bottom:0;inset-inline-end:-.3em;min-width:1.2em;' +
            'border-radius:1em;color:white;text-align:center;font:700 .5em/1.2 system-ui,sans-serif';
        icon.append(badge);
        button.append(icon);
        container.insertBefore(button, container.querySelector('.btnVideoOsdSettings'));
        const model = { button, badge, api, userId, revision: 0 };
        button.onclick = (event) => {
            event.stopPropagation();
            change(model);
        };
        return model;
    }

    function describe(model, action, detail, mark) {
        model.button.setAttribute('aria-label', action);
        model.button.setAttribute('aria-description', detail);
        model.button.title = action + ' · ' + detail;
        model.badge.textContent = mark;
        model.badge.hidden = !mark;
        model.badge.style.backgroundColor = mark === '■' ? '#a62842' : '#37373c';
    }

    function removeSettings(model) {
        model.closeEditor?.(false);
        model.menuEntry?.remove();
        model.menuEntry = null;
    }

    function addSettingsEntry(model) {
        if (!model?.authorized || model.menuEntry?.isConnected) return;
        // Jellyfin Web 12.4 uses this untranslated ID only in the player gear sheet.
        const stats = document.querySelector('.dialogContainer .actionSheet:not(.hide) .actionSheetMenuItem[data-id="stats"]');
        if (!stats) return;
        const sheet = stats.closest('.actionSheet');
        const entry = stats.cloneNode(true);
        entry.classList.add('jtb-player-settings');
        entry.dataset.id = 'rtmp-broadcast-settings';
        entry.querySelector('.actionSheetItemText').textContent = 'Broadcast settings';
        const enter = event => {
            // Keep the native sheet open, including Jellyfin's focus scope and Back handling.
            event.stopPropagation();
            if (panel === model && model.authorized && !model.closeEditor) openEditor(model, sheet, entry);
        };
        entry.onclick = enter;
        entry.onkeydown = event => {
            if (event.key === ' ') {
                event.preventDefault();
                enter(event);
            }
        };
        stats.after(entry);
        model.menuEntry = entry;
        if (sheet.style.position === 'fixed' && sheet.style.top) {
            const top = Math.min(parseFloat(sheet.style.top), innerHeight - sheet.offsetHeight - 8);
            sheet.style.top = Math.max(8, top) + 'px';
        }
        sheet.addEventListener('close', () => {
            if (model.menuEntry === entry) removeSettings(model);
        }, { once: true });
    }

    async function openEditor(model, sheet, entry) {
        const menu = entry.closest('.actionSheetContent');
        const previousStyle = sheet.style.cssText;
        const editor = document.createElement('section');
        editor.className = 'jtb-settings-editor';
        editor.style.cssText = 'box-sizing:border-box;width:100%;min-height:0;padding:.8em 1.1em;overflow:auto;overscroll-behavior:contain;text-align:start';
        editor.setAttribute('aria-label', 'Broadcast settings');
        editor.innerHTML = `
            <h2 style="margin:.2em 0">Broadcast settings</h2>
            <p>Changes apply to your next broadcast.</p>
            <form><fieldset disabled style="border:0;padding:0;margin:.8em 0;min-width:0">
                <legend style="padding:0;margin-bottom:.4em;font-weight:bold">Stream quality</legend>
                <label>Output resolution
                    <select name="height" class="emby-select emby-select-withcolor" style="appearance:auto">
                        <option value="480">480p</option><option value="720">720p</option><option value="1080">1080p</option>
                    </select>
                </label>
                <label>Video bitrate (kb/s)
                    <input name="bitrate" class="emby-input" type="number" inputmode="numeric" min="500" max="12000" step="1" required>
                </label>
                <small>Twitch 1080p at 30 fps · 4,500 kb/s</small>
                <div><button type="submit" class="raised emby-button" disabled>Save quality</button></div>
                <small role="status"></small>
            </fieldset></form>
            <form><fieldset disabled style="border:0;padding:0;margin:.8em 0;min-width:0">
                <legend style="padding:0;margin-bottom:.4em;font-weight:bold">Twitch chat overlay</legend>
                <label><input name="enabled" type="checkbox"> Show Twitch chat on the broadcast</label>
                <label style="display:block;margin-top:.35em">Twitch channel
                    <input name="channel" class="emby-input" type="text" autocomplete="off" autocapitalize="none" spellcheck="false">
                </label>
                <div><button type="submit" class="raised emby-button" disabled>Save chat settings</button></div>
                <small role="status"></small>
            </fieldset></form>
            <p role="status">Loading settings…</p>
            <button type="button" class="raised emby-button">Back</button>`;
        menu.classList.add('hide');
        sheet.style.cssText = 'position:relative;inset:auto;margin:0;width:min(25rem,92vw);max-height:90vh';
        sheet.append(editor);
        const back = editor.querySelector('button[type="button"]');
        back.focus({ preventScroll: true });

        model.closeEditor = (focus = true) => {
            editor.remove();
            sheet.style.cssText = previousStyle;
            menu.classList.remove('hide');
            model.closeEditor = null;
            if (focus && entry.isConnected) entry.focus();
        };
        back.onclick = () => model.closeEditor();
        editor.onkeydown = event => {
            if (event.key === 'Back' || event.key === 'BrowserBack') return;
            if (event.key.startsWith('Arrow') && event.target.matches('button') && editor.closest('.layout-tv')) return;
            // The video OSD listens on document for Space, arrows and single-letter shortcuts.
            // Keep native field editing and button activation inside the settings sheet.
            event.stopPropagation();
            if (event.key === 'Escape') {
                event.preventDefault();
                model.closeEditor();
                return;
            }
            if (event.key !== 'Tab') return;
            const controls = editor.querySelectorAll('select:enabled, input:enabled, button:enabled');
            const first = controls[0];
            if ((event.shiftKey && event.target === first) || (!event.shiftKey && event.target === back)) {
                event.preventDefault();
                (event.shiftKey ? back : first).focus();
            }
        };
        const [quality, chat] = editor.querySelectorAll('form');
        const message = editor.querySelector('p[role="status"]');
        const { height, bitrate } = quality.elements;
        const { enabled, channel } = chat.elements;
        const current = () => editor.isConnected && model === panel && model.authorized
            && window.ApiClient === model.api && model.api.getCurrentUserId() === model.userId;
        if (!current()) {
            model.closeEditor(false);
            return;
        }
        try {
            const saved = await request(model.api, 'Status');
            if (!current()) return;
            height.value = String(saved.outputHeight);
            bitrate.value = String(saved.videoBitrateKbps);
            enabled.checked = saved.chatEnabled;
            channel.value = saved.chatChannel;
            channel.required = enabled.checked;
            message.textContent = '';
        } catch (error) {
            message.textContent = error.message;
            return;
        }
        for (const form of [quality, chat]) {
            const fields = form.querySelector('fieldset');
            const save = form.querySelector('button');
            const feedback = form.querySelector('[role="status"]');
            fields.disabled = false;
            form.oninput = () => {
                channel.required = enabled.checked;
                save.disabled = false;
                feedback.textContent = '';
            };
            form.onsubmit = async event => {
                event.preventDefault();
                if (!current() || fields.disabled || save.disabled) return;
                const name = channel.value.trim().replace(/^#/, '').toLowerCase();
                if (form === chat && (enabled.checked || channel.value.trim()) && !/^[a-z0-9_]{1,25}$/.test(name)) {
                    feedback.textContent = 'Use 1 to 25 letters, numbers or underscores for the channel name.';
                    channel.focus();
                    return;
                }
                if (form.contains(document.activeElement)) back.focus({ preventScroll: true });
                fields.disabled = true;
                feedback.textContent = 'Saving…';
                try {
                    if (form === quality) {
                        await request(model.api, 'Quality', 'POST', { outputHeight: Number(height.value), videoBitrateKbps: bitrate.valueAsNumber });
                    } else {
                        await request(model.api, 'Chat', 'POST', { enabled: enabled.checked, channel: name });
                        channel.value = name;
                    }
                    save.disabled = true;
                    feedback.textContent = 'Saved for your next broadcast.';
                } catch (error) {
                    feedback.textContent = error.message;
                } finally {
                    fields.disabled = false;
                }
            };
        }
        if (document.activeElement === back) height.focus({ preventScroll: true });
    }

    async function refresh(model) {
        if (model.reading || model.busy || model.denied) return;
        const revision = model.revision;
        model.reading = true;
        try {
            const status = await request(model.api, 'Status');
            const sessions = await request(model.api, 'Sessions');
            if (panel !== model || model.busy || revision !== model.revision) return;
            model.authorized = true;
            addSettingsEntry(model);
            const device = model.api.deviceId();
            const matches = sessions.filter(session => device && session.deviceId === device);
            const own = matches.length === 1 ? matches[0] : null;
            const airing = status.active && own && status.sessionId === own.id;
            model.action = airing ? 'Stop' : 'Start';
            model.session = own;
            model.button.setAttribute('aria-pressed', String(Boolean(airing)));
            model.button.disabled = !status.configured || !own || (status.active && !airing);
            let detail = own ? 'Off air · ' + own.itemName : 'Off air';
            if (!status.configured) detail = 'Set a destination in RTMP Broadcast settings.';
            else if (status.active && !airing) detail = 'Another session is broadcasting.';
            else if (!own) detail = 'Play a local library video in this player to enable broadcasting.';
            else if (airing) detail = (status.state === 'playing' ? 'On air' : 'RTMP output · ' + status.state) + ' · ' + own.itemName;
            let mark = status.active ? '■' : '';
            if (/connect|preparing|starting|stopping/i.test(status.state)) mark = '…';
            if (model.issue || !status.configured || /unavailable|error|failed/i.test(status.state)) mark = '!';
            describe(model, airing ? 'End broadcast' : 'Start broadcast', model.issue || detail, mark);
            model.button.classList.remove('hide');
        } catch (error) {
            if (panel !== model || model.busy || revision !== model.revision) return;
            model.button.disabled = true;
            // Plugin endpoints enforce Jellyfin administrator access.
            model.denied = error.status === 401 || error.status === 403 || error.status === 404;
            if (model.denied) {
                model.authorized = false;
                removeSettings(model);
            }
            model.button.classList.toggle('hide', model.denied);
            model.button.setAttribute('aria-pressed', 'false');
            describe(model, 'Start broadcast', 'Broadcast status unavailable.', '!');
        } finally {
            model.reading = false;
        }
    }

    async function change(model) {
        if (model.busy || model.button.disabled || !model.session) return;
        model.revision++;
        model.busy = model.button.disabled = true;
        model.issue = '';
        const { api, action, session } = model;
        try {
            if (window.ApiClient !== api || api.getCurrentUserId() !== model.userId) throw new Error('The signed-in Jellyfin account changed.');
            if (action === 'Stop') {
                const status = await request(api, 'Status');
                if (status.sessionId !== session.id) throw new Error('Broadcast session changed.');
                await request(api, 'Stop', 'POST');
            } else {
                await request(api, 'Start', 'POST', { sessionId: session.id, itemId: session.itemId });
            }
        } catch (error) {
            model.issue = error.message;
        } finally {
            model.busy = false;
            await refresh(model);
        }
    }

    function poll() {
        const controls = document.querySelector('#videoOsdPage:not(.hide) .videoOsdBottom-maincontrols .osdControls');
        const container = controls?.querySelector('.buttons');
        const api = window.ApiClient;
        const userId = api?.getCurrentUserId?.();
        if (panel && (panel.button.parentNode !== container || panel.api !== api || panel.userId !== userId)) {
            removeSettings(panel);
            panel.button.remove();
            panel = null;
        }
        if (!container || !userId || document.hidden) return;
        // A browser may still have the previous userscript running after an upgrade.
        if ([...controls.querySelectorAll('.jtb-player-control')].some(control => control !== panel?.button)) {
            if (panel) removeSettings(panel);
            panel?.button.remove();
            panel = null;
            return;
        }
        if (!panel) panel = mount(container, api, userId);
        refresh(panel);
    }

    // dialogHelper appends a fresh .dialogContainer directly to body for each gear sheet.
    new MutationObserver(records => {
        if (!records.some(r => [...r.addedNodes].some(n => n.matches?.('.dialogContainer')))) return;
        if (!panel?.authorized) poll();
        addSettingsEntry(panel);
    }).observe(document.body, { childList: true });
    poll();
    setInterval(poll, 2500);
})();
