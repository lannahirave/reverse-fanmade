"""Dance event exchange between players in the same session."""
import http.client
import json
import threading
import time
from matchmaking_flow import Harness, join_body
from private_match_contract import create_body


def play(member=0, motion=24):
    return {'action': 'play', 'member': member, 'bank': 20000, 'motion': motion, 'loop': True,
            'char': 'jill', 'name': 'Robot Dance', 'smuggled': {'x': 1}}


def main():
    h = Harness(extra_env={'Matchmaking__JoinLifetimeSeconds': '30'})
    try:
        host, guest, outsider = h.account('em-host'), h.account('em-guest'), h.account('em-out')
        h.connect(host), h.connect(guest), h.connect(outsider)
        info = h.request('POST', '/v1/gameSession', host, create_body(host))['gameSessions'][0]
        h.request('POST', '/v1/gameSession/member/players', guest, join_body(guest),
                  headers={'X-Be-Session-Id': '', 'X-Be-Session-Keyword': info['keyword']})
        h.request('POST', '/v1/gameSession', outsider, create_body(outsider))

        h.request('GET', '/relay/emote?wait=0', expected=401)
        h.request('POST', '/relay/emote', body=play(), expected=401)
        h.request('PUT', '/relay/emote', host, play(), expected=405)
        h.request('POST', '/relay/emote', host, {'action': 'spin', 'member': 0}, expected=400)
        h.request('POST', '/relay/emote', host, {'action': 'play', 'member': 0, 'bank': 1, 'motion': 1}, expected=400)
        h.request('POST', '/relay/emote', host, dict(play(), name='x' * 2000), expected=413)
        h.request('POST', '/relay/emote', host, play(), headers={'Content-Type': 'text/plain'}, expected=415)
        h.request('GET', '/relay/emote?after=-1', host, expected=400)
        h.request('GET', '/relay/emote?wait=99', host, expected=400)

        base = h.request('GET', '/relay/emote?wait=0', guest)
        assert base['events'] == [] and len(base['epoch']) == 32, base

        result = {}

        def poll():
            started = time.monotonic()
            result['body'] = h.request('GET', '/relay/emote?after=0&wait=20', guest)
            result['elapsed'] = time.monotonic() - started
        t = threading.Thread(target=poll)
        t.start()
        time.sleep(.5)
        assert t.is_alive(), 'poll should wait'
        sent = h.request('POST', '/relay/emote', host, play())
        assert sent['delivered'] == 1 and sent['epoch'] == base['epoch'], sent
        t.join(10)
        assert not t.is_alive() and result['elapsed'] < 5, result
        events = result['body']['events']
        assert len(events) == 1, events
        ev = events[0]
        assert ev['from'] == host['id'] and ev['action'] == 'play' and ev['motion'] == 24 and 'smuggled' not in ev, ev

        assert h.request('GET', '/relay/emote?wait=0', host)['events'] == []
        assert h.request('GET', '/relay/emote?wait=0', outsider)['events'] == []
        assert h.request('POST', '/relay/emote', outsider, play())['delivered'] == 0
        h.request('POST', '/relay/emote', host, {'action': 'stop', 'member': 0})
        later = h.request('GET', f'/relay/emote?after={ev["id"]}&wait=0', guest)['events']
        assert [e['action'] for e in later] == ['stop'] and later[0]['id'] > ev['id'], later

        assert h.request('POST', '/relay/emote', guest, play(member=1, motion=51))['delivered'] == 1
        back = h.request('GET', '/relay/emote?wait=0', host)['events']
        assert len(back) == 1 and back[0]['member'] == 1 and back[0]['motion'] == 51, back

        print('PASS emote contract')
    finally:
        h.close()


if __name__ == '__main__':
    main()
