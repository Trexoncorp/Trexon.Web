#!/usr/bin/env python3
"""Exercise the complete sample over real loopback HTTP, then stop it and remove test data."""
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import urllib.parse

ROOT = Path(__file__).resolve().parents[1]

def main():
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    api_key = secrets.token_hex(32)
    admin_password = secrets.token_urlsafe(24)
    with tempfile.TemporaryDirectory(prefix='trexon-web-smoke-') as directory:
        env = dict(os.environ, TREXON_URL=base, TREXON_DATA_DIR=directory,
                   TREXON_JWT_KEY=secrets.token_hex(32), TREXON_API_KEY=api_key,
                   TREXON_ADMIN_PASSWORD=admin_password)
        with open(Path(directory) / 'server.log', 'w+') as log:
            process = subprocess.Popen(['dotnet', str(ROOT / 'examples/Trexon.Web.Sample/bin/Release/net8.0/Trexon.Web.Sample.dll')],
                                       cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT)
            try:
                def call(method, path, body=None, token=None, headers=None, expected=200):
                    values = dict(headers or {})
                    if token:
                        values['Authorization'] = 'Bearer ' + token
                    data = None
                    if body is not None:
                        data = json.dumps(body).encode()
                        values['Content-Type'] = 'application/json'
                    request = urllib.request.Request(base + path, data=data, method=method, headers=values)
                    try:
                        response = urllib.request.urlopen(request, timeout=15)
                    except urllib.error.HTTPError as error:
                        response = error
                    with response:
                        content = response.read()
                        assert response.status == expected, f'{method} {path}: expected {expected}, got {response.status}'
                        parsed = json.loads(content) if 'json' in response.headers.get('Content-Type', '') and content else content.decode()
                        return parsed, response.headers
                for attempt in range(120):
                    if process.poll() is not None:
                        raise RuntimeError('Sample exited before becoming ready')
                    try:
                        call('GET', '/health')
                        break
                    except (OSError, urllib.error.URLError, AssertionError):
                        time.sleep(.25)
                else:
                    raise TimeoutError('Sample did not start within 30 seconds')
                document, _ = call('GET', '/openapi.json')
                assert '/tasks' in document['paths']
                assert {'Bearer', 'ApiKey', 'Cookies'} <= document['components']['securitySchemes'].keys()
                ui, _ = call('GET', '/docs/index.html')
                assert 'swagger-ui' in ui
                console, _ = call('GET', '/docs/ws')
                assert '<html lang="en">' in console
                console_config, _ = call('GET', '/docs/ws/config.json')
                console_root = base + '/docs/ws/'
                assert urllib.parse.urljoin(console_root, console_config['schemaUrl']) == base + '/openapi.json'
                assert urllib.parse.urljoin(console_root, console_config['auth']['loginUrl']) == base + '/auth/cookie/login'
                _, assets = call('GET', '/docs/ws/console.js')
                assert 'javascript' in assets['Content-Type']
                assert '[Open WebSocket console](./ws)' in document['info']['description']
                assert any(item['path'] == '/ws/chat/{room}' for item in document['x-trexon-websockets'])
                assert any(item['path'] == '/ws/private/{room}' for item in document['x-trexon-websockets'])
                chat, _ = call('GET', '/chat')
                assert 'new WebSocket' in chat and 'X-CSRF-Token' in chat
                call('GET', '/ws/chat/general', expected=400)
                call('GET', '/ws/private/general', expected=401)
                call('GET', '/tasks', expected=401)
                password = secrets.token_urlsafe(20)
                for name in ['alice', 'bob']:
                    call('POST', '/auth/register', {'username': name, 'password': password, 'roles': ['Admin']}, expected=201)
                alice, _ = call('POST', '/auth/login', {'username': 'alice', 'password': password})
                bob, _ = call('POST', '/auth/login', {'username': 'bob', 'password': password})
                access = alice['accessToken']
                assert alice['user']['roles'] == ['User']
                call('POST', '/tasks', {'title': ''}, token=access, expected=400)
                call('POST', '/tasks', {'title': '   '}, token=access, expected=400)
                task, headers = call('POST', '/tasks', {'title': 'Explore Trexon.Web'}, token=access, expected=201)
                assert headers['X-Task-Module'] == 'enabled'
                assert headers['Location'] == f'/tasks/{task["id"]}'
                call('GET', f'/tasks/{task["id"]}', token=bob['accessToken'], expected=404)
                call('PUT', f'/tasks/{task["id"]}', {'title': 'Explore complete sample', 'completed': True}, token=access)
                tasks, _ = call('GET', '/tasks?completed=true&take=10', token=access)
                assert len(tasks) == 1 and tasks[0]['completed']
                call('GET', '/tasks?take=1000', token=access, expected=400)
                call('GET', '/demo/admin', token=access, expected=403)
                admin, _ = call('POST', '/auth/login', {'username': 'admin', 'password': admin_password})
                call('GET', '/demo/admin', token=admin['accessToken'])
                call('GET', '/demo/policy', token=admin['accessToken'])
                call('GET', '/demo/integration', headers={'X-API-Key': api_key})
                call('GET', '/demo/integration', token=access, expected=401)
                call('GET', '/demo/headers', headers={'X-Client': 'smoke'})
                one, _ = call('GET', '/demo/cached')
                two, _ = call('GET', '/demo/cached')
                assert one == two
                call('GET', '/demo/conflict', expected=409)
                problem, _ = call('GET', '/demo/failure', expected=500)
                assert 'detail' not in problem and problem['traceId']
                call('DELETE', f'/tasks/{task["id"]}', token=access, expected=204)
                replacement, _ = call('POST', '/auth/refresh', {'refreshToken': alice['refreshToken']})
                call('POST', '/auth/refresh', {'refreshToken': alice['refreshToken']}, expected=401)
                call('POST', '/auth/refresh', {'refreshToken': replacement['refreshToken']}, expected=401)
                call('POST', '/auth/logout', {'refreshToken': bob['refreshToken']}, expected=204)
                call('POST', '/auth/refresh', {'refreshToken': bob['refreshToken']}, expected=401)
                print('Sample smoke test passed: docs, validation, CRUD, ownership, JWT, roles, policies, API keys, cache, errors, refresh/revoke.')
            except Exception:
                log.seek(0)
                # Server output must never contain environment secrets, even on failed runs.
                output = log.read()
                for secret in [api_key, admin_password, env['TREXON_JWT_KEY']]:
                    output = output.replace(secret, '[redacted]')
                print(output[-10000:])
                raise
            finally:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()

if __name__ == '__main__':
    main()
