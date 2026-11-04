"""Real backend persistence API regression. Synthetic bytes/quest IDs are NOT game saves."""
import base64
import concurrent.futures
import copy
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

def main():
    subprocess.run(['dotnet', 'build', '-c', 'Debug', '--no-restore', '--nologo'], cwd=ROOT / 'backend', check=True)
    folder = ROOT / 'integration-tests/artifacts' / ('battlepass-' + uuid.uuid4().hex)
    folder.mkdir(parents=True)
    database = folder / 'accounts.db'
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    env = dict(os.environ, Relay__Enabled='true', Steam__Mode='fallback', Relay__DatabasePath=str(database),
               ASPNETCORE_URLS=f'http://127.0.0.1:{port}', Capture__LogDirectory=str(folder))
    output = (folder / 'backend.log').open('w')
    def start():
        return subprocess.Popen(['dotnet', str(ROOT / 'backend/bin/Debug/net10.0/ReVerse.Capture.dll')],
                                cwd=ROOT / 'backend', env=env, stdout=output, stderr=output,
                                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    def call(method, path, token=None, body=None, content_type='application/json'):
        headers = {'Content-Type': content_type}
        if token: headers['X-Relay-Session-Token'] = token
        connection = http.client.HTTPConnection('127.0.0.1', port, timeout=30)
        try:
            connection.request(method, path, headers=headers, body=json.dumps(body).encode() if body is not None else None)
            response = connection.getresponse()
            raw = response.read()
            return response.status, json.loads(raw) if raw else {}
        finally: connection.close()
    def ready():
        deadline = time.monotonic() + 20
        while True:
            try:
                assert call('GET', '/ready')[0] == 401
                return
            except OSError:
                assert process.poll() is None
                if time.monotonic() > deadline: raise
                time.sleep(.1)
    def login(secret):
        status, result = call('POST', '/relay/account/login', body={'username': 'player-' + secret, 'secretKey': secret})
        assert status == 200, result
        return result
    process = start()
    try:
        ready()
        alice, bob = login('alice'), login('bob')
        a, b = alice['sessionToken'], bob['sessionToken']

        with sqlite3.connect(database) as db:
            assert db.execute("SELECT count(*) FROM sqlite_master WHERE name='save_snapshots'").fetchone()[0] == 0
        assert call('GET', '/relay/saves/server', a)[0] == 404
        assert call('GET', '/relay/saves/server')[0] == 401
        assert call('GET', '/relay/saves/server', 'F' * 64)[0] == 401
        raw_payload = b'opaque-related-save-container\x00\xff' + bytes(range(256)) * 100
        bp = {
            'challengeQuestList': [
                {'uiIndex': 8-i, 'questID': str(2**64-1-i), 'questBeginTime': str(-2**63+i),
                 'currentQuestPoint': str(2**64-1-i), 'isPremium': i % 2 == 0} for i in range(9)],
            'accumulatedCP': str(2**63-1), 'freeRewardCPLevel': 2, 'premiumRewardCPLevel': 3,
            'isCurrentPremiumBattlePassBought': True, 'accumulatedStarCP': str(2**63-1),
            'lastUsedStarLevel': 1, 'statStarLevelAccumlatedCPTotal': str(2**63-1),
            'statStarLevelCompleteCountTUReset': 4, 'statStarLevelCompleteCountTotal': 5,
            'statStarLevelGotRPBoosterFree': 6, 'statStarLevelGotRPBoosterPremium': 7, 'lastPatchTime': 8
        }
        write = {'requestId': uuid.uuid4().hex, 'expectedRevision': 0,
                 'payload': base64.b64encode(raw_payload).decode(), 'buildId': 'synthetic-build',
                 'formatVersion': 'opaque-test-1', 'catalogueId': 'synthetic-catalogue',
                 'clientSaveHash': 'unknown-client-hash-value', 'battlePass': bp}
        status, first = call('PUT', '/relay/saves/server', a, write)
        assert status == 200, (status, first)
        assert first['revision'] == 1 and first['battlePass'] == bp
        assert first['payloadDigest'] == hashlib.sha256(raw_payload).hexdigest().upper()
        assert base64.b64decode(first['payload']) == raw_payload
        assert call('PUT', '/relay/saves/server', a, write) == (200, first)
        assert call('GET', '/relay/saves/server', b)[0] == 404
        assert call('GET', '/relay/saves/other', a)[0] == 404
        conflict = dict(write, payload=base64.b64encode(b'other').decode())
        assert call('PUT', '/relay/saves/server', a, conflict)[0] == 409
        assert call('PUT', '/relay/saves/server', a, dict(write, requestId=uuid.uuid4().hex))[0] == 409
        bad = copy.deepcopy(write); bad['requestId'] = uuid.uuid4().hex; bad['expectedRevision'] = 1
        bad['battlePass']['challengeQuestList'].pop()
        assert call('PUT', '/relay/saves/server', a, bad)[0] == 400
        bad['battlePass']['challengeQuestList'].append(None)
        assert call('PUT', '/relay/saves/server', a, bad)[0] == 400
        bad = copy.deepcopy(write); bad['requestId'] = uuid.uuid4().hex; bad['expectedRevision'] = 1
        bad['battlePass']['challengeQuestList'][0]['questID'] = str(2**64)
        assert call('PUT', '/relay/saves/server', a, bad)[0] == 400
        bad['battlePass']['challengeQuestList'][0]['questID'] = 123
        assert call('PUT', '/relay/saves/server', a, bad)[0] == 400
        bad = copy.deepcopy(write); del bad['battlePass']['lastPatchTime']
        assert call('PUT', '/relay/saves/server', a, bad)[0] == 400
        assert call('PUT', '/relay/saves/server', a, {})[0] == 400
        assert call('GET', '/relay/saves/server?revision=-1', a)[0] == 400
        assert call('DELETE', '/relay/saves/server', a)[0] == 405
        assert call('PUT', '/relay/saves/server', a, write, 'text/plain')[0] == 415
        too_large = dict(write, payload=base64.b64encode(b'x' * (8 * 1024 * 1024 + 1)).decode())
        assert call('PUT', '/relay/saves/server', a, too_large)[0] == 413

        candidates = [dict(write, requestId=uuid.uuid4().hex, expectedRevision=1,
                           payload=base64.b64encode(b'writer-' + bytes([i])).decode()) for i in range(2)]
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(lambda w: call('PUT', '/relay/saves/server', a, w), candidates))
        assert sorted(s for s, _ in results) == [200, 409], results
        assert call('GET', '/relay/saves/server', a)[1]['revision'] == 2

        assert call('PUT', '/relay/saves/server', a, write) == (200, first)
        assert call('GET', '/relay/saves/server', a)[1]['revision'] == 2

        restore = dict(write, requestId=uuid.uuid4().hex, expectedRevision=2)
        assert call('PUT', '/relay/saves/server', a, restore)[1]['revision'] == 3

        opaque = dict(write, requestId=uuid.uuid4().hex); opaque.pop('battlePass')
        assert call('PUT', '/relay/saves/server', b, opaque)[0] == 200

        with sqlite3.connect(database) as db:
            db.execute("CREATE TRIGGER fail_quest BEFORE INSERT ON battlepass_quests WHEN NEW.ordinal=8 BEGIN SELECT RAISE(ABORT, 'test rollback'); END")
        failing = dict(write, requestId=uuid.uuid4().hex, expectedRevision=3)
        assert call('PUT', '/relay/saves/server', a, failing)[0] == 500
        with sqlite3.connect(database) as db:
            db.execute('DROP TRIGGER fail_quest')
            assert db.execute('SELECT count(*) FROM save_snapshots WHERE account_id=?', (alice['accountId'],)).fetchone()[0] == 3
            assert db.execute('SELECT count(*) FROM battlepass_progress WHERE account_id=?', (alice['accountId'],)).fetchone()[0] == 3
            assert db.execute('SELECT count(*) FROM battlepass_quests WHERE account_id=?', (alice['accountId'],)).fetchone()[0] == 27
            assert db.execute('SELECT revision FROM save_heads WHERE account_id=?', (alice['accountId'],)).fetchone()[0] == 3
            assert db.execute('SELECT accumulated_cp,last_patch_time FROM battlepass_progress WHERE account_id=? AND revision=1', (alice['accountId'],)).fetchone() == (2**63-1, 8)
            quests = db.execute('SELECT quest_id,current_quest_point,quest_begin_time,typeof(quest_id) FROM battlepass_quests WHERE account_id=? AND revision=1 ORDER BY ordinal', (alice['accountId'],)).fetchall()
            assert quests[0] == (str(2**64-1), str(2**64-1), -2**63, 'text')
            assert db.execute('PRAGMA foreign_key_check').fetchall() == []
        process.terminate(); process.wait(timeout=10)
        process = start(); ready()
        a = login('alice')['sessionToken']
        assert call('GET', '/relay/saves/server?revision=1', a) == (200, first)
        latest = call('GET', '/relay/saves/server', a)[1]
        assert latest['revision'] == 3 and latest['payload'] == first['payload'] and latest['battlePass'] == bp
        assert call('GET', '/relay/saves/server', b)[1]['battlePass'] is None
        with sqlite3.connect(database) as db:
            assert db.execute('SELECT count(*) FROM save_schema_versions WHERE version=1').fetchone()[0] == 1
            assert db.execute('SELECT count(*) FROM accounts').fetchone()[0] == 2
        print('PASS snapshot bytes, all battlepass fields, UInt64/Int64 boundaries, nine quests, retries, stale/concurrent writes, atomic rollback, restore, migration, restart, account/slot isolation')
    finally:
        process.terminate()
        try: process.wait(timeout=10)
        except subprocess.TimeoutExpired: process.kill(); process.wait()
        output.close()

if __name__ == '__main__': main()
