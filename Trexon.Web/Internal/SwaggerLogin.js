/* Adds local-account login alongside Swagger's standard Bearer/API-key controls.
   Loaded as a named Swagger UI plugin before the UI is constructed. */
window.TrexonLoginPlugin = function (system) {
    const React = system.React;
    const h = React.createElement;

    return {
        wrapComponents: {
            auths: Original => class TrexonAuths extends React.Component {
                state = { busy: false, message: '', failed: false };
                controller = null;

                componentWillUnmount() {
                    this.controller?.abort();
                }

                submit = async event => {
                    event.preventDefault();
                    if (this.controller) return;
                    const form = event.currentTarget;
                    const data = new FormData(form);
                    const controller = new AbortController();
                    this.controller = controller;
                    this.setState({ busy: true, message: '', failed: false });
                    try {
                        // Resolve relative to /docs/index.html to retain reverse-proxy PathBase.
                        const url = new URL(system.getConfigs().trexonLoginUrl, window.location.href);
                        if (url.origin !== window.location.origin) throw new Error('Login must use the same origin.');
                        const response = await fetch(url, {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            credentials: 'omit',
                            redirect: 'error',
                            cache: 'no-store',
                            signal: controller.signal,
                            body: JSON.stringify({ username: data.get('username'), password: data.get('password') })
                        });
                        const result = await response.json();
                        if (!response.ok) {
                            const fields = Object.values(result.errors || {}).flat().join(' ');
                            throw new Error(result.detail || fields || result.title || 'Не удалось войти.');
                        }
                        if (typeof result.accessToken !== 'string' || !result.accessToken) {
                            throw new Error('Сервер не вернул accessToken.');
                        }
                        if (controller.signal.aborted) return;
                        // The standard Bearer scheme adds Authorization to subsequent Try it out requests.
                        // Keep only the access token in Swagger's in-memory state; do not persist credentials.
                        system.preauthorizeApiKey('Bearer', result.accessToken);
                        this.setState({ message: 'Вход выполнен. Bearer-токен применён к запросам.', failed: false });
                    } catch (error) {
                        if (!controller.signal.aborted) {
                            this.setState({ message: error.message || 'Не удалось войти.', failed: true });
                        }
                    } finally {
                        form.elements.namedItem('password').value = '';
                        this.controller = null;
                        if (!controller.signal.aborted) this.setState({ busy: false });
                    }
                };

                render() {
                    // A scheme-specific API-key dialog should not offer unrelated Bearer login.
                    if (!this.props.definitions.has('Bearer')) return h(Original, this.props);
                    return h('div', null,
                        h('div', { className: 'auth-container', 'data-trexon-login': true },
                            h('h4', null, 'Вход по логину и паролю'),
                            h('p', null, 'Используйте существующий аккаунт. JWT будет подставлен автоматически.'),
                            h('form', { onSubmit: this.submit },
                                h('fieldset', { disabled: this.state.busy, style: { border: 0, padding: 0 } },
                                    h('label', { htmlFor: 'trexon-username' }, 'Логин'),
                                    h('input', {
                                        id: 'trexon-username', name: 'username', type: 'text',
                                        autoComplete: 'username', required: true, maxLength: 64,
                                        style: { display: 'block', width: '100%', marginBottom: '12px' }
                                    }),
                                    h('label', { htmlFor: 'trexon-password' }, 'Пароль'),
                                    h('input', {
                                        id: 'trexon-password', name: 'password', type: 'password',
                                        autoComplete: 'current-password', required: true, maxLength: 128,
                                        style: { display: 'block', width: '100%', marginBottom: '12px' }
                                    }),
                                    h('button', { type: 'submit', className: 'btn authorize' },
                                        this.state.busy ? 'Вход…' : 'Войти')
                                )
                            ),
                            h('p', {
                                role: this.state.failed ? 'alert' : 'status',
                                style: { color: this.state.failed ? '#b42318' : 'inherit' }
                            }, this.state.message)
                        ),
                        h(Original, this.props)
                    );
                }
            }
        }
    };
};
