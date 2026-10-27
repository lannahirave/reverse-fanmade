"""Private room creation, admission and lifecycle contracts."""
import json
import socket
import string
import time
from matchmaking_flow import Harness, ticket, event, offer, join_body
from game_join_contract import expected_profiles


def create_body(account, capacity=2):
    return {'gameSession': {
        'maxPlayers': capacity, 'maxSpectators': 0,
        'member': {'players': [{'accountId': account['id'], 'joinState': 'JOINED',
                                'customData1': json.dumps({'version': 1, 'nonce': 7}, separators=(',', ':'))}],
                   'spectators': []},
        'joinDisabled': False, 'supportedService': ['steam'],
        'customData1': '', 'customData2': '', 'useCrossPlay': False, 'usePlayerSession': False,
        'keywordType': {'length': {}, 'charType': 'x', 'charOmitType': 'y', 'valid': 'z'},
        'regions': [{'name': 'local'}]}}


def main():
    h = Harness(extra_env={'Matchmaking__JoinLifetimeSeconds': '30'})
    try:
        host, guest, outsider = h.account('pm-host'), h.account('pm-guest'), h.account('pm-out')
        wh, wg, wo = h.connect(host), h.connect(guest), h.connect(outsider)
        created = h.request('POST', '/v1/gameSession', host, create_body(host))
        sessions = created['gameSessions']
        assert len(sessions) == 1, created
        info = sessions[0]
        assert isinstance(info['sessionId'], str) and len(info['sessionId']) == 32
        assert type(info['gameSessionSequenceNo']) is int
        assert isinstance(info['serviceEncryptionKey'], str)
        assert len(info['keyword']) == 4 and set(info['keyword']) <= set(string.digits), info
        assert [p['accountId'] for p in info['member']['players']] == [host['id']]
        sid, keyword = info['sessionId'], info['keyword']
        event(wh, 'players:created')
        assert h.request('GET', '/v1/gameSession', host, headers={'X-Be-Session-Ids': sid})['gameSessions'][0]['sessionId'] == sid


        for bad in ({}, {'gameSession': {}}, {'gameSession': []}, {'gameSession': [{}, {}]},
                    {'gameSession': {'maxPlayers': 1, 'member': {'players': [{'accountId': outsider['id']}]}}},
                    {'gameSession': {'maxPlayers': 11, 'member': {'players': [{'accountId': outsider['id']}]}}},
                    {'gameSession': {'maxPlayers': 2, 'member': {'players': []}}},
                    {'gameSession': {'maxPlayers': 2, 'member': {'players': [{'accountId': guest['id']}]}}},
                    {'gameSession': {'maxPlayers': 2, 'member': {'players': [{'accountId': outsider['id']}]}, 'bogus': 1}},
                    {'gameSession': {'maxPlayers': 2, 'member': {'players': [{'accountId': outsider['id']}]}}, 'bogus': 1},
                    {'regions': 'RVS-B-WW', 'gameSession': {'maxPlayers': 2, 'member': {'players': [{'accountId': outsider['id']}]}}},
                    {'gameSession': {'maxPlayers': 2, 'member': {'players': [{'accountId': outsider['id']}]}, 'keywordType': 'x'}},
                    {'gameSession': {'maxPlayers': 2, 'member': {'players': [{'accountId': outsider['id']}]}, 'regions': 'x'}}):
            h.request('POST', '/v1/gameSession', outsider, bad, expected=400)

        wrapped = dict(create_body(outsider))
        wrapped['gameSession'] = [wrapped['gameSession']]
        wrapped['regions'] = ['RVS-B-WW']
        other = h.request('POST', '/v1/gameSession', outsider, wrapped)['gameSessions'][0]
        assert len(other['sessionId']) == 32 and len(other['keyword']) == 4
        event(wo, 'players:created')
        h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest),
                  headers={'X-Be-Session-Id': other['sessionId'], 'X-Be-Session-Keyword': keyword}, expected=403)
        h.request('DELETE', '/v1/gameSession', outsider, headers={'X-Be-Session-Id': other['sessionId']})

        h.request('POST', '/v1/gameSession', host, create_body(host), expected=409)
        h.request('POST', '/v1/matchmaking/ticket', host, ticket(host), expected=409)


        headers = {'X-Be-Session-Id': sid}
        h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest), headers=headers, expected=403)
        h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest),
                  headers={**headers, 'X-Be-Session-Keyword': 'WRONG1'}, expected=403)
        h.request('GET', '/v1/gameSession', outsider, headers={'X-Be-Session-Ids': sid}, expected=404)

        other_nonce = dict(join_body(guest))
        other_nonce['players'] = [dict(join_body(guest)['players'][0], customData1=json.dumps({'version': 1, 'nonce': 999}, separators=(',', ':')))]
        h.request('POST', '/v1/gameSession/member/players', guest, other_nonce, headers=headers, expected=403)

        keyed = {**headers, 'X-Be-Session-Keyword': keyword}
        h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest), expected=400)
        h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest),
                  headers={'X-Be-Session-Keyword': '!!!!!'}, expected=404)
        h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest),
                  headers={**keyed, 'X-Be-Session-Id': sid + ',' + sid}, expected=400)
        h.request('GET', '/v1/gameSession', guest, headers={'X-Be-Session-Keyword': keyword}, expected=400)
        reply = h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest),
                          headers={'X-Be-Session-Id': '', 'X-Be-Session-Keyword': keyword})
        assert reply['sessionId'] == sid and reply['keyword'] == keyword, reply
        assert reply['players'][0]['serviceProfiles'] == expected_profiles(guest)
        event(wh, 'players:created'); event(wg, 'players:created')

        assert h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest), headers=keyed) == reply
        assert h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest),
                         headers={'X-Be-Session-Keyword': keyword}) == reply
        changed = dict(join_body(guest))
        changed['players'] = [dict(join_body(guest)['players'][0], customData1=json.dumps({'version': 1, 'nonce': 456}, separators=(',', ':')))]
        h.request('POST', '/v1/gameSession/member/players', guest, changed, headers=keyed, expected=409)

        third = h.account('pm-third')
        wt = h.connect(third)
        h.request('POST', '/v1/gameSession/member/players', third, join_body(third), headers=keyed, expected=409)

        snapshot = h.request('GET', '/v1/gameSession', host, headers={'X-Be-Session-Ids': sid})['gameSessions'][0]
        assert {p['accountId'] for p in snapshot['member']['players']} == {host['id'], guest['id']}
        assert snapshot['representative'] == {'accountId': host['id']}
        assert snapshot['maxPlayers'] == 2
        h.request('PATCH', '/v1/gameSession', guest, {'joinDisabled': True}, headers=headers, expected=403)
        h.request('DELETE', '/v1/gameSession', guest, headers=headers, expected=403)
        h.request('DELETE', '/v1/gameSession', host, headers=headers)
        h.request('GET', '/v1/gameSession', host, headers={'X-Be-Session-Ids': sid}, expected=404)
        h.request('POST', '/v1/matchmaking/ticket', host, ticket(host)); event(wh, 'tickets:submitted')


        other = h.request('POST', '/v1/gameSession', outsider, create_body(outsider))['gameSessions'][0]
        assert other['keyword'] != keyword and len(other['keyword']) == 4
        event(wo, 'players:created')
        records = [json.loads(line) for file in h.folder.glob('requests-*.jsonl') for line in file.read_text().splitlines()]
        blob = json.dumps(records)
        assert keyword not in blob and other['keyword'] not in blob, 'keywords must stay out of logs'
        for private in (host['sessionToken'], guest['sessionToken'], host['gameKey']):
            assert private not in blob
        print('PASS private create schema, four-character keywords, code-only admission, capacity, representative control and redaction')
    finally:
        h.close()


def lifecycle():
    h = Harness(extra_env={'Matchmaking__JoinLifetimeSeconds': '1'})
    try:
        host, guest = h.account('waiting-host'), h.account('late-guest')
        wh, wg = h.connect(host), h.connect(guest)
        info = h.request('POST', '/v1/gameSession', host, create_body(host))['gameSessions'][0]
        event(wh, 'players:created')
        headers = {'X-Be-Session-Id': info['sessionId']}
        keyed = {'X-Be-Session-Keyword': info['keyword']}
        time.sleep(1.4)
        h.request('GET', '/v1/gameSession', host, headers={'X-Be-Session-Ids': info['sessionId']})
        h.request('PATCH', '/v1/gameSession', host, {'joinDisabled': True}, headers=headers)
        event(wh, 'joinDisabled:updated')
        h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest), headers=keyed, expected=409)
        h.request('GET', '/v1/gameSession', guest, headers={'X-Be-Session-Ids': info['sessionId']}, expected=404)
        h.request('PATCH', '/v1/gameSession', host, {'joinDisabled': False}, headers=headers)
        event(wh, 'joinDisabled:updated')
        reply = h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest), headers=keyed)
        assert reply['sessionId'] == info['sessionId']
        event(wh, 'players:created'); event(wg, 'players:created')
        h.request('PATCH', '/v1/gameSession/signaling', host, {'signalingTimeoutSeconds': 1}, headers=headers)
        time.sleep(1.4)
        h.request('GET', '/v1/gameSession', host, headers={'X-Be-Session-Ids': info['sessionId']}, expected=404)
        h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest), headers=keyed, expected=404)
        other = h.request('POST', '/v1/gameSession', host, create_body(host))['gameSessions'][0]
        event(wh, 'players:created')
        wh.close(timeout=.1)
        time.sleep(.4)
        h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest),
                  headers={'X-Be-Session-Keyword': other['keyword']}, expected=404)
        print('PASS private lobby wait, closed-room admission, signaling timeout and host disconnect')
    finally:
        h.close()


def kick_member():
    h = Harness(extra_env={'Matchmaking__JoinLifetimeSeconds': '30'})
    try:
        host, guest, target = [h.account(name) for name in ('kick-host', 'kick-guest', 'kick-target')]
        wh, wg, wt = [h.connect(account) for account in (host, guest, target)]
        info = h.request('POST', '/v1/gameSession', host, create_body(host, capacity=4))['gameSessions'][0]
        event(wh, 'players:created')
        headers = {'X-Be-Session-Id': info['sessionId'], 'X-Be-Session-Keyword': info['keyword']}

        h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest), headers=headers)
        event(wh, 'players:created'); event(wg, 'players:created')
        h.request('POST', '/v1/gameSession/member/players', target, join_body(target), headers=headers)
        event(wh, 'players:created'); event(wg, 'players:created'); event(wt, 'players:created')

        h.request('DELETE', '/v1/gameSession/members', host,
                  headers={**headers, 'X-Be-Account-Id': target['id']})
        deleted = [event(ws, 'players:deleted') for ws in (wh, wg, wt)]
        for message in deleted:
            player = message['data']['data']['member']['players'][0]
            assert player['accountId'] == target['id']
            assert player['joinState'] == 'CLIENT_KILLED', message

        records = [json.loads(line) for file in h.folder.glob('requests-*.jsonl')
                    for line in file.read_text(encoding='utf-8').splitlines()]
        assert any(record.get('method') == 'DELETE' and record.get('path') == '/v1/gameSession/members'
                   and record.get('reason') == '' for record in records), records

        snapshot = h.request('GET', '/v1/gameSession', host,
                             headers={'X-Be-Session-Ids': info['sessionId']})['gameSessions'][0]
        assert {player['accountId'] for player in snapshot['member']['players']} == {host['id'], guest['id']}
        print('PASS host-directed private member removal is a kick and preserves the remaining room')
    finally:
        h.close()


def kick_last_member():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
        probe.bind(('127.0.0.1', 0))
        signaling_port = probe.getsockname()[1]
    h = Harness(extra_env={
        'Matchmaking__JoinLifetimeSeconds': '30',
        'Signaling__Enabled': 'true',
        'Signaling__BindAddress': '127.0.0.1',
        'Signaling__Port': str(signaling_port),
        'Signaling__PublicHost': '127.0.0.1',
        'Signaling__PublicPort': str(signaling_port)
    })
    try:
        host, guest = h.account('last-kick-host'), h.account('last-kick-guest')
        wh, wg = h.connect(host), h.connect(guest)
        info = h.request('POST', '/v1/gameSession', host, create_body(host, capacity=2))['gameSessions'][0]
        event(wh, 'players:created')
        headers = {'X-Be-Session-Id': info['sessionId'], 'X-Be-Session-Keyword': info['keyword']}

        h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest), headers=headers)
        event(wh, 'players:created'); event(wg, 'players:created')
        h.request('PATCH', '/v1/gameSession/signaling', host,
                  {'signalingTimeoutSeconds': 30}, headers={'X-Be-Session-Id': info['sessionId']})
        event(wh, 'signaling:created'); event(wg, 'signaling:created')

        h.request('DELETE', '/v1/gameSession/members', host,
                  headers={**headers, 'X-Be-Account-Id': guest['id']})
        for ws in (wh, wg):
            message = event(ws, 'players:deleted')
            player = message['data']['data']['member']['players'][0]
            assert player['accountId'] == guest['id']
            assert player['joinState'] == 'CLIENT_KILLED', message

        snapshot = h.request('GET', '/v1/gameSession', host,
                             headers={'X-Be-Session-Ids': info['sessionId']})['gameSessions'][0]
        assert [player['accountId'] for player in snapshot['member']['players']] == [host['id']]
        assert snapshot['signaling'] == 'COMPLETED', snapshot
        h.request('GET', '/v1/gameSession', guest,
                  headers={'X-Be-Session-Ids': info['sessionId']}, expected=404)
        h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest), headers=headers)
        event(wh, 'players:created'); event(wg, 'players:created')
        event(wh, 'operateSequenceNo'); event(wg, 'signaling:created')
        snapshot = h.request('GET', '/v1/gameSession', host,
                             headers={'X-Be-Session-Ids': info['sessionId']})['gameSessions'][0]
        assert {player['accountId'] for player in snapshot['member']['players']} == {host['id'], guest['id']}
        print('PASS two-player kick preserves the remaining host session')
    finally:
        h.close()


if __name__ == '__main__':
    main()
    lifecycle()
    kick_member()
    kick_last_member()
