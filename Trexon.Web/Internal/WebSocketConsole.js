/* A dependency-free, same-origin WebSocket client driven by x-trexon-websockets.
   No proxy, credential forwarding, persistent storage or server-side socket registry is added. */
(() => {
    'use strict';
    const el = id => document.getElementById(id);
    const root = new URL('./', location.href);
    let config, documentSchema, endpoints = [], selected, socket = null;
    let sent = 0, received = 0;
    const fields = new Map();

    function log(direction, value, failed = false) {
        const entry = document.createElement('li');
        const heading = document.createElement('div');
        heading.className = 'entry-head';
        const label = document.createElement('span');
        label.className = failed ? 'direction error' : 'direction';
        label.textContent = direction;
        const at = document.createElement('time');
        at.textContent = new Date().toLocaleTimeString();
        heading.append(label, at);
        const body = document.createElement('pre');
        // Never interpret an endpoint's messages or metadata as HTML.
        body.textContent = value;
        entry.append(heading, body);
        el('journal').append(entry);
        while (el('journal').children.length > 200) el('journal').firstElementChild.remove();
        el('empty').hidden = true;
        if (el('follow').checked) el('journal').scrollTop = el('journal').scrollHeight;
        el('counts').textContent = `Sent: ${sent} · Received: ${received}`;
    }

    function state(text, kind = '') {
        el('state').textContent = text;
        el('state').dataset.state = kind;
        const busy = socket !== null;
        const connected = socket?.readyState === WebSocket.OPEN;
        for (const id of ['endpoint', 'path']) el(id).disabled = busy || !selected;
        for (const field of fields.values()) field.disabled = busy;
        el('connect').disabled = busy || !selected;
        el('disconnect').disabled = !socket || socket.readyState >= WebSocket.CLOSING;
        el('send').disabled = !connected;
    }

    function dereference(schema) {
        const visited = new Set();
        while (schema?.$ref?.startsWith('#/') && !visited.has(schema.$ref)) {
            visited.add(schema.$ref);
            schema = schema.$ref.slice(2).split('/').reduce((value, key) =>
                value?.[key.replace(/~1/g, '/').replace(/~0/g, '~')], documentSchema);
        }
        return schema || {};
    }

    function example(schema, depth = 0) {
        if (depth > 5) return null;
        schema = dereference(schema);
        if (schema.example !== undefined) return schema.example;
        if (schema.default !== undefined) return schema.default;
        if (schema.enum?.length) return schema.enum[0];
        if (schema.allOf) return Object.assign({}, ...schema.allOf.map(part => example(part, depth + 1)));
        if (schema.oneOf || schema.anyOf) return example((schema.oneOf || schema.anyOf)[0], depth + 1);
        if (schema.type === 'object' || schema.properties) {
            return Object.fromEntries(Object.entries(schema.properties || {})
                .filter(([, value]) => !value.readOnly).map(([key, value]) => [key, example(value, depth + 1)]));
        }
        if (schema.type === 'array') return [example(schema.items, depth + 1)];
        if (schema.type === 'boolean') return false;
        if (schema.type === 'integer' || schema.type === 'number') return Math.max(schema.minimum || 0, 0);
        if (schema.format === 'date-time') return new Date().toISOString();
        if (schema.format === 'date') return new Date().toISOString().slice(0, 10);
        if (schema.format === 'uuid') return '00000000-0000-4000-8000-000000000001';
        return 'string'.padEnd(Math.min(schema.minLength || 0, 200), 'x').slice(0, schema.maxLength ?? 200);
    }

    function setExample() {
        el('message').value = selected.inputFormat === 'text' ? 'Hello' : JSON.stringify(example(selected.input), null, 2);
        el('send-error').textContent = '';
    }

    function connectionUrl() {
        const path = el('path').value;
        if (!path.startsWith('/') || path.startsWith('//') || /[{}]/.test(path)) {
            throw new Error('Enter a path starting with / and fill in the route parameters.');
        }
        const appUrl = new URL(config.appUrl, root);
        const url = new URL(path.slice(1), appUrl);
        if (url.origin !== location.origin) throw new Error('Choose an endpoint in this application.');
        url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
        return url;
    }

    function preview() {
        try { el('url').textContent = connectionUrl().href; }
        catch { el('url').textContent = 'Fill in the route parameters to connect.'; }
    }

    function updatePath() {
        // Structured route parts come from ASP.NET Core's parser, including constraints,
        // optional separators and catch-all parameters; no regex parser is needed in the browser.
        const segments = selected.segments.map(parts => {
            const result = [];
            for (let index = 0; index < parts.length; index++) {
                const part = parts[index];
                if (part.parameter) {
                    const value = fields.get(part.parameter).value || part.default || '';
                    if (!value && (part.optional || part.catchAll)) {
                        if (parts[index - 1]?.separator) result.pop();
                    } else if (!value) result.push('{' + part.parameter + '}');
                    else result.push(part.catchAll ? value.split('/').map(encodeURIComponent).join('/') : encodeURIComponent(value));
                } else result.push(part.text);
            }
            return result.join('');
        }).filter(Boolean);
        el('path').value = '/' + segments.join('/');
        preview();
    }

    function chooseEndpoint() {
        selected = endpoints[Number(el('endpoint').value)];
        if (!selected) return;
        el('summary').textContent = selected.summary || selected.path;
        el('description').textContent = selected.description || '';
        const rules = selected.authorization || [];
        el('access').textContent = selected.allowAnonymous ? 'Access: anonymous' : rules.length
            ? 'Sign-in required' + rules.map(rule => [rule.roles && `roles: ${rule.roles}`, rule.policy && `policy: ${rule.policy}`, rule.schemes && `schemes: ${rule.schemes}`].filter(Boolean).join(', ')).filter(Boolean).map(text => ` · ${text}`).join('')
            : 'Access: application defaults';
        el('parameters').replaceChildren();
        fields.clear();
        for (const part of selected.segments.flat().filter(part => part.parameter)) {
            if (fields.has(part.parameter)) continue;
            const label = document.createElement('label');
            label.textContent = part.parameter + (part.optional || part.catchAll || part.default != null ? ' · optional' : ' · path parameter');
            const input = document.createElement('input');
            input.id = 'parameter-' + fields.size;
            label.htmlFor = input.id;
            input.value = part.default ?? '';
            input.required = !part.optional && !part.catchAll && part.default == null;
            input.addEventListener('input', updatePath);
            fields.set(part.parameter, input);
            el('parameters').append(label, input);
        }
        el('message-label').textContent = 'Message body · ' + (selected.inputFormat === 'text' ? 'text' : 'JSON');
        el('validate-json').disabled = selected.inputFormat === 'text';
        el('message').disabled = false;
        el('example').disabled = false;
        el('input-schema').textContent = JSON.stringify(dereference(selected.input), null, 2);
        el('output-schema').textContent = JSON.stringify(dereference(selected.output), null, 2);
        updatePath();
        setExample();
        state('Disconnected');
    }

    el('connect-form').addEventListener('submit', event => {
        event.preventDefault();
        if (socket || !selected) return;
        el('notice').textContent = '';
        try {
            const url = connectionUrl();
            const active = new WebSocket(url);
            socket = active;
            state('Connecting…');
            log('CONNECTING', url.href);
            active.onopen = () => {
                if (socket !== active) return;
                state('Connected');
                log('OPEN', 'Connection is ready to send messages.');
            };
            active.onmessage = event => {
                if (socket !== active) return;
                received++;
                let value = event.data;
                let failed = false;
                if (typeof value === 'string') {
                    try { const parsed = JSON.parse(value); failed = !!parsed?.error; value = JSON.stringify(parsed, null, 2); }
                    catch { /* Plain-text endpoints need no JSON decoding. */ }
                } else value = '[Binary message]';
                log('RECEIVED', value, failed);
            };
            active.onerror = () => {
                if (socket !== active) return;
                el('notice').textContent = 'The connection failed. Check the path, access and HTTPS. Browsers do not expose the HTTP response when a WebSocket handshake is rejected.';
                state('Error', 'error');
            };
            active.onclose = event => {
                if (socket !== active) return;
                socket = null;
                state('Disconnected', event.code === 1000 ? '' : 'error');
                log('CLOSED', `Code ${event.code}${event.reason ? ' · ' + event.reason : ''}`, event.code !== 1000);
            };
        } catch (error) { el('notice').textContent = error.message; }
    });

    function disconnect() {
        if (!socket || socket.readyState >= WebSocket.CLOSING) return;
        socket.close(1000, 'Console disconnect');
        state('Disconnecting…');
    }
    el('disconnect').addEventListener('click', disconnect);
    el('endpoint').addEventListener('change', chooseEndpoint);
    el('path').addEventListener('input', preview);
    el('example').addEventListener('click', setExample);
    el('send-form').addEventListener('submit', event => {
        event.preventDefault();
        if (socket?.readyState !== WebSocket.OPEN) return;
        el('send-error').textContent = '';
        try {
            const value = el('message').value;
            if (selected.inputFormat !== 'text' && el('validate-json').checked) JSON.parse(value);
            socket.send(value);
            sent++;
            log('SENT', value);
        } catch (error) { el('send-error').textContent = error.message; }
    });
    el('clear').addEventListener('click', () => {
        el('journal').replaceChildren();
        el('empty').hidden = false;
        sent = received = 0;
        el('counts').textContent = 'Sent: 0 · Received: 0';
    });

    async function json(url, options) {
        const response = await fetch(new URL(url, root), { credentials: 'same-origin', cache: 'no-store', ...options });
        const result = await response.json();
        if (!response.ok) throw new Error(result.detail || Object.values(result.errors || {}).flat().join(' ') || result.title || `HTTP ${response.status}`);
        return result;
    }

    async function cookieMutation(url, body) {
        const csrf = await json(config.auth.csrfUrl);
        return fetch(new URL(url, root), {
            method: 'POST', credentials: 'same-origin', cache: 'no-store',
            headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken },
            body: body === undefined ? undefined : JSON.stringify(body)
        });
    }

    let authBusy = false;
    async function changeAccount(login) {
        if (authBusy) return;
        authBusy = true;
        el('auth-error').textContent = '';
        el('login').disabled = el('logout').disabled = true;
        try {
            const response = await cookieMutation(login ? config.auth.loginUrl : config.auth.logoutUrl,
                login ? { username: el('username').value, password: el('password').value } : undefined);
            if (!response.ok) {
                const problem = await response.json();
                throw new Error(problem.detail || Object.values(problem.errors || {}).flat().join(' ') || problem.title);
            }
            // Existing sockets retain their handshake identity; reconnect after a session change.
            disconnect();
            el('auth-status').textContent = login ? `Signed in as ${(await response.json()).username}. Connect to an endpoint.` : 'Not signed in.';
        } catch (error) { el('auth-error').textContent = error.message; }
        finally {
            el('password').value = '';
            authBusy = false;
            el('login').disabled = el('logout').disabled = false;
        }
    }
    el('login-form').addEventListener('submit', event => { event.preventDefault(); void changeAccount(true); });
    el('logout').addEventListener('click', () => { void changeAccount(false); });
    window.addEventListener('pagehide', () => socket?.close(1000, 'Page closed'));

    async function initialize() {
        try {
            config = await json('config.json');
            el('title').textContent = config.title;
            document.title = config.title + ' · WebSocket console';
            el('http-docs').href = new URL(config.docsUrl, root).href;
            el('schema-link').href = new URL(config.schemaUrl, root).href;
            documentSchema = await json(config.schemaUrl);
            endpoints = documentSchema['x-trexon-websockets'] || [];
            el('endpoint').replaceChildren();
            for (const [index, endpoint] of endpoints.entries()) {
                const option = document.createElement('option');
                option.value = index;
                option.textContent = endpoint.path;
                el('endpoint').append(option);
            }
            if (endpoints.length) chooseEndpoint();
            else {
                const option = document.createElement('option');
                option.textContent = 'No WebSocket endpoints';
                el('endpoint').append(option);
                el('notice').textContent = 'Register a handler with AddSocket<THandler>() and its endpoint will appear here automatically.';
            }
            if (config.auth && location.protocol === 'https:') {
                el('login-form').hidden = false;
                try { el('auth-status').textContent = 'Signed in as ' + (await json(config.auth.meUrl)).username; }
                catch { el('auth-status').textContent = 'Sign in to use protected endpoints. Create an account through HTTP / Swagger.'; }
            } else if (config.auth) el('auth-status').textContent = 'Public endpoints are ready. Open the application over HTTPS to sign in with cookies.';
        } catch (error) { el('notice').textContent = 'Could not load documentation: ' + error.message; }
    }
    void initialize();
})();
