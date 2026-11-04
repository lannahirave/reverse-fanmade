"""Username accounts, SQLite storage, database upgrade, and game session binding regression."""
import concurrent.futures
import hashlib
import http.client
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]

LEGACY_SCHEMAS = {
    'original': """
        CREATE TABLE accounts (
            id TEXT PRIMARY KEY, username TEXT NOT NULL COLLATE BINARY,
            salt BLOB NOT NULL, hash BLOB NOT NULL,
            algorithm TEXT NOT NULL, iterations INTEGER NOT NULL,
            created_at INTEGER NOT NULL
        );
        CREATE INDEX accounts_username ON accounts(username);
        """,
    'keyed': """
        CREATE TABLE accounts (
            id TEXT PRIMARY KEY, username TEXT NOT NULL COLLATE BINARY,
            salt BLOB NOT NULL, hash BLOB NOT NULL,
            algorithm TEXT NOT NULL, iterations INTEGER NOT NULL,
            created_at INTEGER NOT NULL, username_key TEXT
        );
        """,
}

def main():
    subprocess.run(['dotnet', 'build', '-c', 'Debug', '--nologo', '--no-restore'], cwd=ROOT / 'backend', check=True)
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    folder = ROOT / 'integration-tests' / 'artifacts' / ('accounts-' + uuid.uuid4().hex)
    folder.mkdir(parents=True)
    database = folder / 'accounts.db'
    output = (folder / 'backend.log').open('w')
    def start(db=database, logs=folder):
        env = dict(os.environ, Relay__Enabled='true', Steam__Mode='fallback', Relay__DatabasePath=str(db),
                   ASPNETCORE_URLS=f'http://127.0.0.1:{port}', Capture__LogDirectory=str(logs))
        return subprocess.Popen(['dotnet', str(ROOT / 'backend/bin/Debug/net10.0/ReVerse.Capture.dll')],
                                cwd=ROOT / 'backend', env=env, stdout=output, stderr=output,
                                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    def request(method, path, headers=None, body=None):
        connection = http.client.HTTPConnection('127.0.0.1', port, timeout=30)
        try:
            connection.request(method, path, body=body, headers=headers or {})
            response = connection.getresponse()
            payload = response.read()
            return response.status, json.loads(payload) if payload else {}
        finally: connection.close()
    def ready():
        deadline = time.monotonic() + 20
        while True:
            try:
                assert request('GET', '/ready')[0] == 401
                return
            except OSError:
                assert process.poll() is None
                if time.monotonic() >= deadline: raise
                time.sleep(0.1)
    def login(name, **extra):
        status, body = request('POST', '/relay/account/login', {'Content-Type': 'application/json'},
                               json.dumps(dict(extra, username=name)))
        assert status == 200, (status, body)
        return body
    def stop():
        process.terminate()
        try: process.wait(timeout=10)
        except subprocess.TimeoutExpired: process.kill(); process.wait()
    def headers(account): return {'X-Relay-Session-Token': account['sessionToken']}
    process = start()
    try:
        ready()
        first = login('Alex')
        started = time.monotonic()
        again = login('ALEX')
        assert time.monotonic() - started < 2
        assert first['created'] and not again['created'] and again['username'] == 'Alex'
        assert first['accountId'] == again['accountId'] != first['sessionToken']
        assert login('  aLeX ')['accountId'] == login('Ａｌｅｘ')['accountId'] == first['accountId']
        assert login('Alex', secretKey='left over from an old relay')['accountId'] == first['accountId']
        second = login('Alexa')
        assert second['created'] and second['accountId'] != first['accountId']
        unicode_account = login('Боб 🎮')
        assert login('БОБ 🎮')['accountId'] == unicode_account['accountId']
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
            parallel = list(pool.map(lambda _: login('Concurrent'), range(4)))
        assert len({a['accountId'] for a in parallel}) == 1 and sum(a['created'] for a in parallel) == 1
        tokens = []
        for account in [first, second, unicode_account]:
            status, body = request('POST', '/v1/steam-steam/sign/RVS-B-WW', headers(account))
            assert status == 200, body
            tokens.append(body['rebe_token'])
        assert len(set(tokens)) == 3
        for account, token in zip([first, second, unicode_account], tokens):
            session = dict(headers(account), Authorization='Bearer ' + token)
            status, body = request('GET', '/v1/verify/test', session)
            assert status == 200 and body['id_token'] == {
                'sub': account['accountId'], 'sub_nickname': account['username']}, body
        assert request('GET', '/v1/verify/test', dict(headers(first), Authorization='Bearer ' + tokens[1]))[0] == 401
        assert request('POST', '/v1/steam-steam/sign/RVS-B-WW', {'X-Relay-Player-Id': first['accountId']})[0] == 401
        assert request('GET', '/ready', {'X-Relay-Session-Token': 'F' * 64})[0] == 401
        assert request('POST', '/relay/account/login', {'Content-Type': 'application/json'}, '{')[0] == 400
        assert request('POST', '/relay/account/login', {'Content-Type': 'application/json'}, '{"username":"   "}')[0] == 400
        assert request('POST', '/relay/account/login', {'Content-Type': 'application/json'}, json.dumps({'username': 'x' * 65}))[0] == 400
        assert request('POST', '/relay/account/login', {'Content-Type': 'application/json'}, 'x' * 9000)[0] == 413
        with sqlite3.connect(database) as db:
            assert [r[1] for r in db.execute("PRAGMA table_info('accounts')")] == ['id', 'username', 'username_key', 'created_at']
            assert db.execute("SELECT count(*) FROM accounts WHERE username_key='ALEX'").fetchone()[0] == 1
            db.execute('UPDATE relay_sessions SET expires_at=0 WHERE token_hash=?', (hashlib.sha256(second['sessionToken'].encode()).digest(),))
        assert request('GET', '/ready', headers(second))[0] == 401
        stop()
        process = start(); ready()
        assert login('Alex')['accountId'] == first['accountId']
        assert request('POST', '/v1/steam-steam/sign/RVS-B-WW', headers(first))[0] == 200
        for path in folder.iterdir():
            if path.is_file():
                assert first['sessionToken'].encode() not in path.read_bytes(), path
        print('PASS username is the account (case-insensitive, no password), concurrency, Unicode, persistence, expiry, session isolation, no token capture')
        stop()

        for schema_name, schema in LEGACY_SCHEMAS.items():
            legacy = folder / f'legacy-{schema_name}.db'
            keyed = schema_name == 'keyed'
            with sqlite3.connect(legacy) as db:
                db.executescript(schema + """
                    CREATE TABLE relay_sessions (
                        token_hash BLOB PRIMARY KEY, account_id TEXT NOT NULL REFERENCES accounts(id),
                        expires_at INTEGER NOT NULL
                    );
                    """)
                def legacy_row(name, created_at):
                    account_id = uuid.uuid4().hex
                    values = [account_id, name, os.urandom(32), os.urandom(32), 'PBKDF2-SHA256', 600000, created_at]
                    if keyed: values.append(name.upper())
                    db.execute(f'INSERT INTO accounts VALUES ({",".join("?" * len(values))})', values)
                    return account_id
                oldest = legacy_row('Krados', 100)
                active = legacy_row('krados', 200)
                newest = legacy_row('krados', 300)
                taken = legacy_row('krados#2', 50)
                solo = legacy_row('Solo', 10)
                session_token = os.urandom(32).hex().upper()
                db.execute('INSERT INTO relay_sessions VALUES (?,?,?)',
                           (hashlib.sha256(session_token.encode()).digest(), active, 4102444800))
            process = start(legacy, folder / f'legacy-{schema_name}-logs'); ready()
            status, body = request('GET', '/ready', {'X-Relay-Session-Token': session_token})
            assert status != 401, (schema_name, status, body)
            assert login('KRADOS')['accountId'] == active
            with sqlite3.connect(legacy) as db:
                names = dict(db.execute('SELECT id, username FROM accounts'))
                assert names == {active: 'krados', newest: 'krados#3', oldest: 'Krados#4', taken: 'krados#2', solo: 'Solo'}, names
                assert [r[1] for r in db.execute("PRAGMA table_info('accounts')")] == ['id', 'username', 'username_key', 'created_at']
                assert db.execute("SELECT count(*) FROM sqlite_master WHERE name='accounts_upgraded' OR name='accounts_username'").fetchone()[0] == 0
                assert db.execute('PRAGMA foreign_key_check').fetchall() == []
            assert login('krados#3')['accountId'] == newest and login('Krados#4')['accountId'] == oldest
            assert login('solo')['accountId'] == solo
            stop()
            process = start(legacy, folder / f'legacy-{schema_name}-logs'); ready()
            assert login('krados')['accountId'] == active
            with sqlite3.connect(legacy) as db:
                assert dict(db.execute('SELECT id, username FROM accounts')) == names
            stop()
            print(f'PASS {schema_name} account database upgraded once; most recent sign-in keeps the name; renamed accounts and sessions kept')
    finally:
        stop()
        output.close()

if __name__ == '__main__': main()
