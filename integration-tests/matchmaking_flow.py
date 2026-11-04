"""Synthetic control-plane contracts, not proof of stock-client gameplay.

Uses websocket-client. Each run uses an isolated port, database and log directory.
"""
import http.client
import json
import os
from pathlib import Path
import socket
import subprocess
import time
import uuid

import websocket

ROOT = Path(__file__).resolve().parents[1]


class Harness:
    def __init__(self, experimental=True, extra_env=None):
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            self.port = sock.getsockname()[1]
        self.folder = ROOT / 'integration-tests/artifacts' / ('matchmaking-' + uuid.uuid4().hex)
        self.folder.mkdir(parents=True)
        self.output = (self.folder / 'backend.log').open('w', encoding='utf-8')
        self.env = dict(os.environ, Relay__Enabled='true', Steam__Mode='fallback', Signaling__Enabled='false',
                        Signaling__NegativeControlReply='',
                        Relay__DatabasePath=str(self.folder / 'accounts.db'),
                        ASPNETCORE_URLS=f'http://127.0.0.1:{self.port}',
                        Capture__LogDirectory=str(self.folder), Capture__MaxCapturedBodyBytes='8',
                        Matchmaking__ExperimentalSessionProtocol=str(experimental).lower(),
                        Matchmaking__Rulesets__test='2', Matchmaking__TicketLifetimeSeconds='3',
                        Matchmaking__JoinLifetimeSeconds='3')
        self.env.update(extra_env or {})
        self.sockets = []
        self.start()

    def start(self):
        backend_dll = os.environ.get('REVERSE_TEST_BACKEND_DLL', str(ROOT / 'backend/bin/Debug/net10.0/ReVerse.Capture.dll'))
        backend_exe = os.environ.get('REVERSE_TEST_BACKEND_EXE')
        command = [backend_exe] if backend_exe else ['dotnet', backend_dll]
        self.proc = subprocess.Popen(command,
                                     cwd=ROOT / 'backend', env=self.env, stdout=self.output, stderr=self.output,
                                     creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        deadline = time.monotonic() + 20
        while True:
            try:
                self.request('GET', '/ready', expected=401)
                break
            except OSError:
                assert self.proc.poll() is None, self.folder
                if time.monotonic() >= deadline:
                    raise
                time.sleep(.1)

    def request(self, method, path, account=None, body=None, headers=None, expected=200):
        merged = {'Content-Type': 'application/json'}
        if account:
            merged['X-Relay-Session-Token'] = account['sessionToken']
        merged.update(headers or {})
        connection = http.client.HTTPConnection('127.0.0.1', self.port, timeout=20)
        try:
            connection.request(method, path, json.dumps(body) if body is not None else None, merged)
            response = connection.getresponse()
            payload = response.read()
            assert response.status == expected, (method, path, response.status, payload)
            return json.loads(payload) if payload else {}
        finally:
            connection.close()

    def account(self, username=None):
        account = self.request('POST', '/relay/account/login', body={'username': username or 'player-' + uuid.uuid4().hex[:12], 'secretKey': uuid.uuid4().hex})
        account['id'] = account['accountId']
        token = self.request('POST', '/v1/steam-steam/sign/RVS-B-WW', account)['rebe_token']
        account['gameKey'] = self.request('POST', '/v1/open/reverse', account, headers={'Authorization': 'Bearer ' + token})['session_key']
        return account

    def connect(self, account):
        ws = websocket.create_connection(f'ws://127.0.0.1:{self.port}/',
                                          header={'X-Relay-Session-Token': account['sessionToken']}, timeout=5)
        self.sockets.append(ws)

        command = json.dumps({'command': 'session_assign', 'args': {'sessionKey': account['gameKey']}})
        ws.send_frame(websocket.ABNF.create_frame(command[:7], websocket.ABNF.OPCODE_TEXT, fin=0))
        ws.send_frame(websocket.ABNF.create_frame(command[7:], websocket.ABNF.OPCODE_CONT, fin=1))
        assert json.loads(ws.recv()) == {'dataFormatType': 'CMD_RESPONSE', 'command': 'session_assign'}
        return ws

    def stop(self):
        self.proc.terminate()
        self.proc.wait(timeout=10)

    def close(self):
        for ws in self.sockets:
            try:
                ws.close(timeout=.1)
            except OSError:
                pass
        self.stop()
        self.output.close()


def ticket(account, build=1, region='lan', ruleset='test'):
    return {'rulesetName': ruleset, 'useCrossPlay': False, 'groupName': 'public',
            'ticketAttributes': [{'name': 'build', 'type': 1, 'value': build}],
            'players': [{'accountId': account['id'], 'playerAttributes': []}],
            'playarea': {'name': 'local', 'regions': [{'name': region, 'latency': 1}]}}


def event(ws, suffix):
    value = json.loads(ws.recv())
    assert value['dataFormatType'] == 'MSG_DATA' and value['dataType'].endswith(suffix), value
    assert type(value['seq']) is int and value['seq'] > 0
    return value


def offer(ws):
    return event(ws, 'offers:created')['data']['data']['offers'][0]['location']['gameSessionId']


def cancel(h, account, response):
    return h.request('DELETE', '/v1/matchmaking/ticket', account, headers={'X-Be-Ticket-Id': response['ticketId']})


def join_body(account):
    return {'players': [{'accountId': account['id'], 'joinState': 'JOINED',
                         'customData1': '{"version":1,"nonce":123}'}], 'useCrossPlay': False}


def main():
    h = Harness()
    try:
        a, b, outsider = [h.account() for _ in range(3)]
        assert a['gameKey'] != b['gameKey']
        h.request('POST', '/v1/matchmaking/ticket', a, ticket(a), expected=409)
        wa, wb, wo = [h.connect(p) for p in (a, b, outsider)]
        h.request('POST', '/v1/matchmaking/ticket', a, ticket(b), expected=403)
        h.request('POST', '/v1/matchmaking/ticket', a, ticket(a), headers={'X-Auth-Session-Key': b['gameKey']}, expected=401)
        h.request('POST', '/v1/matchmaking/ticket', a, {'rulesetName': 'bad'}, expected=400)
        h.request('POST', '/v1/matchmaking/ticket', a, {'padding': 'x' * 270000}, expected=413)
        h.request('PUT', '/v1/matchmaking/ticket', a, expected=405)
        h.request('POST', '/v1/matchmaking/unknown', a, expected=404)
        ta = h.request('POST', '/v1/matchmaking/ticket', a, ticket(a), headers={'X-Auth-Session-Key': a['gameKey']})
        assert set(ta) == {'matchmakingTicketSequenceNo', 'ticketId', 'rulesetName', 'submitRetryWaitSeconds'}
        assert type(ta['matchmakingTicketSequenceNo']) is int
        assert h.request('POST', '/v1/matchmaking/ticket', a, ticket(a)) == ta
        ea = event(wa, 'tickets:submitted')
        assert ea['seq'] == 1
        h.request('POST', '/v1/matchmaking/ticket', a, ticket(a, build=2), expected=409)
        h.request('DELETE', '/v1/matchmaking/ticket', outsider, headers={'X-Be-Ticket-Id': ta['ticketId']}, expected=404)
        tb = h.request('POST', '/v1/matchmaking/ticket', b, ticket(b))
        event(wb, 'tickets:submitted')
        session = offer(wa)
        assert offer(wb) == session
        assert h.request('POST', '/v1/matchmaking/ticket', a, ticket(a)) == ta
        headers = {'X-Be-Session-Id': session}
        read_headers = {'X-Be-Session-Ids': session}
        h.request('GET', '/v1/gameSession', outsider, headers=read_headers, expected=404)
        h.request('GET', '/v1/gameSession', a, expected=400)
        h.request('GET', '/v1/gameSession/signaling', a, headers=headers, expected=503)
        h.request('PUT', '/v1/gameSession/signaling', a, headers=headers, expected=405)
        h.request('POST', '/v1/gameSession', outsider, {}, expected=400)
        h.request('POST', '/v1/gameSession/member/players', a, join_body(b), headers=headers, expected=403)
        for account, ws in ((a, wa), (b, wb)):
            h.request('POST', '/v1/gameSession/member/players', account, join_body(account), headers=headers)
            h.request('POST', '/v1/gameSession/member/players', account, join_body(account), headers=headers)
            event(wa, 'players:created')
            event(wb, 'players:created')
        snapshot = h.request('GET', '/v1/gameSession', a, headers=read_headers)['gameSessions'][0]
        assert {p['accountId'] for p in snapshot['member']['players']} == {a['id'], b['id']}
        assert snapshot['representative'] == {'accountId': a['id']} and snapshot['gameSessionSequenceNo'] == 3
        assert h.request('GET', '/v1/gameSession', b, headers=read_headers)['gameSessions'][0] == snapshot
        h.request('PATCH', '/v1/gameSession', b, {'joinDisabled': True}, headers=headers, expected=403)
        h.request('PATCH', '/v1/gameSession', a, {'joinDisabled': True, 'customData1': 123}, headers=headers, expected=400)
        assert not h.request('GET', '/v1/gameSession', a, headers=read_headers)['gameSessions'][0]['joinDisabled']
        h.request('PATCH', '/v1/gameSession', a, {'joinDisabled': True}, headers=headers)
        event(wa, 'joinDisabled:updated'); event(wb, 'joinDisabled:updated')
        signal = {'endpoints': [{'protocol': 'test-only', 'identityHint': 'fixture', 'psk': 'never-log-this-psk',
                                'server': {'host': '192.0.2.1', 'port': 9000}}], 'secret': 'never-log-this-secret'}
        h.request('PATCH', '/v1/gameSession/signaling', b, {'signaling': signal}, headers=headers, expected=403)
        h.request('PATCH', '/v1/gameSession/signaling', a, {'signaling': signal}, headers=headers, expected=400)
        h.request('GET', '/v1/gameSession/signaling', b, headers=headers, expected=503)

        assert h.request('GET', '/v1/gameSession', b, headers=read_headers)['gameSessions'][0]['signaling'] == 'NONE'
        h.request('POST', '/v1/gameSession/sessionMessage', a, {'content': 'opaque-secret-message'}, headers=headers)
        message = event(wa, 'sessionMessage:created')
        event(wb, 'sessionMessage:created')
        assert message['data']['data']['from'] == a['id']
        wa.send(json.dumps({'command': 'retransmission', 'args': {'seq': message['seq'], 'lost_seqs': [1, message['seq']]}}))
        assert json.loads(wa.recv()) == ea
        assert json.loads(wa.recv()) == message
        assert json.loads(wa.recv())['command'] == 'retransmission'

        wo.settimeout(.2)
        try:
            unexpected = wo.recv()
            raise AssertionError(unexpected)
        except websocket.WebSocketTimeoutException:
            pass
        wo.settimeout(5)
        wb.close(timeout=.1)
        event(wa, 'offers:failed'); event(wa, 'tickets:failed')
        h.request('GET', '/v1/gameSession', a, headers=read_headers, expected=404)

        h.request('POST', '/v1/matchmaking/ticket', b, ticket(b), expected=409)
        wb = h.connect(b)
        ta = h.request('POST', '/v1/matchmaking/ticket', a, ticket(a, ruleset='not-configured'))
        event(wa, 'tickets:submitted')
        cancel(h, a, ta); cancel(h, a, ta)
        event(wa, 'tickets:canceled')
        fresh = h.request('POST', '/v1/matchmaking/ticket', a, ticket(a, ruleset='not-configured'))
        assert fresh['ticketId'] != ta['ticketId']
        event(wa, 'tickets:submitted'); event(wa, 'tickets:timedOut')

        for mismatch in ('build', 'region', 'crossplay'):
            ra, rb = ticket(a), ticket(b)
            if mismatch == 'build': rb = ticket(b, build=2)
            if mismatch == 'region': rb = ticket(b, region='elsewhere')
            if mismatch == 'crossplay': rb['useCrossPlay'] = True
            ta = h.request('POST', '/v1/matchmaking/ticket', a, ra)
            tb = h.request('POST', '/v1/matchmaking/ticket', b, rb)
            event(wa, 'tickets:submitted'); event(wb, 'tickets:submitted')
            event(wa, 'tickets:timedOut'); event(wb, 'tickets:timedOut')

        h.request('POST', '/v1/matchmaking/ticket', a, ticket(a)); event(wa, 'tickets:submitted')
        h.request('POST', '/v1/matchmaking/ticket', b, ticket(b)); event(wb, 'tickets:submitted')
        stale = offer(wa); assert offer(wb) == stale
        for ws in (wa, wb):
            event(ws, 'offers:failed'); event(ws, 'tickets:failed')
        h.request('GET', '/v1/gameSession', a, headers={'X-Be-Session-Ids': stale}, expected=404)

        c = h.account()
        wc, wc2 = h.connect(c), h.connect(c)
        rc = ticket(c, ruleset='not-configured')
        tc = h.request('POST', '/v1/matchmaking/ticket', c, rc)
        assert event(wc, 'tickets:submitted')['seq'] == 1
        assert event(wc2, 'tickets:submitted')['seq'] == 1
        wc.close(timeout=.1)
        assert h.request('POST', '/v1/matchmaking/ticket', c, rc) == tc
        cancel(h, c, tc)
        assert event(wc2, 'tickets:canceled')['seq'] == 2

        for _ in range(65):
            tc = h.request('POST', '/v1/matchmaking/ticket', c, rc)
            event(wc2, 'tickets:submitted')
            cancel(h, c, tc)
            event(wc2, 'tickets:canceled')
        wc2.send(json.dumps({'command': 'retransmission', 'args': {'lost_seqs': [1]}}))
        try:
            assert wc2.recv() == ''
        except (websocket.WebSocketConnectionClosedException, ConnectionResetError):
            pass

        h.stop(); h.start()
        h.request('DELETE', '/v1/matchmaking/ticket', a, headers={'X-Be-Ticket-Id': ta['ticketId']}, expected=404)
        h.request('GET', '/v1/gameSession', a, headers={'X-Be-Session-Ids': stale}, expected=404)
        for path in h.folder.glob('*.jsonl'):
            contents = path.read_text(encoding='utf-8')
            for private in ('never-log-this-psk', 'never-log-this-secret', 'opaque-secret-message', a['sessionToken'], a['gameKey']):
                assert private not in contents, path
        print('PASS tickets, typed request validation, retries, cancellation, expiry, account isolation, shared offers,')
        print('     session state, correct verbs, signaling upload rejection, bounded replay, multiple sockets, disconnect and restart')
    finally:
        h.close()
    h = Harness(experimental=False)
    try:
        a, b = h.account(), h.account()
        wa, wb = h.connect(a), h.connect(b)
        for account, ws in ((a, wa), (b, wb)):
            h.request('POST', '/v1/matchmaking/ticket', account, ticket(account))
            event(ws, 'tickets:submitted')
        event(wa, 'tickets:timedOut'); event(wb, 'tickets:timedOut')
        h.request('GET', '/v1/gameSession', a, expected=501)
        print('PASS safe default: no experimental match offers or fabricated signaling')
    finally:
        h.close()


if __name__ == '__main__':
    main()
