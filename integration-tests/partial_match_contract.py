"""Dynamic match size: a ruleset count is the target, and a smaller compatible group
launches after the ruleset queue has been unchanged for PartialMatchWaitSeconds.

Synthetic control-plane contract, not proof of stock-client gameplay.
"""
import time

import websocket

from matchmaking_flow import Harness, event, join_body, offer, ticket, cancel

WAIT = 2


def quiet(ws, seconds):
    ws.settimeout(seconds)
    try:
        unexpected = ws.recv()
        raise AssertionError(unexpected)
    except websocket.WebSocketTimeoutException:
        pass
    finally:
        ws.settimeout(5)


def submit(h, account, ws):
    response = h.request('POST', '/v1/matchmaking/ticket', account, ticket(account))
    event(ws, 'tickets:submitted')
    return response


def session_size(h, account, session):
    snapshot = h.request('GET', '/v1/gameSession', account, headers={'X-Be-Session-Ids': session})['gameSessions'][0]
    return snapshot['maxPlayers']


def close_session(h, representative, session, sockets):
    h.request('DELETE', '/v1/gameSession', representative, headers={'X-Be-Session-Id': session})
    for ws in sockets:
        event(ws, 'offers:failed'); event(ws, 'tickets:failed')


def main():
    env = {'Matchmaking__Rulesets__test': '4', 'Matchmaking__PartialMatchWaitSeconds': str(WAIT),
           'Matchmaking__TicketLifetimeSeconds': '30', 'Matchmaking__JoinLifetimeSeconds': '10'}
    h = Harness(extra_env=env)
    try:
        a, b, c, d, e = [h.account() for _ in range(5)]
        wa, wb, wc, wd, we = [h.connect(p) for p in (a, b, c, d, e)]

        # Full target count launches at once, with maxPlayers equal to the target.
        for account, ws in ((a, wa), (b, wb), (c, wc), (d, wd)):
            submit(h, account, ws)
        session = offer(wa)
        assert all(offer(ws) == session for ws in (wb, wc, wd))
        assert session_size(h, a, session) == 4
        close_session(h, a, session, (wa, wb, wc, wd))

        # Three queue and one leaves: the two left must not launch before the
        # queue has been quiet for WAIT seconds, then launch as a 2-player session.
        submit(h, a, wa); submit(h, b, wb)
        tc = submit(h, c, wc)
        cancel(h, c, tc); event(wc, 'tickets:canceled')
        left = time.monotonic()
        quiet(wa, WAIT - 0.6)
        session = offer(wa)
        waited = time.monotonic() - left
        assert offer(wb) == session
        assert waited >= WAIT - 0.1, waited
        assert session_size(h, b, session) == 2
        headers = {'X-Be-Session-Id': session}
        for account in (a, b):
            h.request('POST', '/v1/gameSession/member/players', account, join_body(account), headers=headers)
            event(wa, 'players:created'); event(wb, 'players:created')
        h.request('POST', '/v1/gameSession/member/players', c, join_body(c), headers=headers, expected=409)
        players = h.request('GET', '/v1/gameSession', a, headers={'X-Be-Session-Ids': session})['gameSessions'][0]['member']['players']
        assert {p['accountId'] for p in players} == {a['id'], b['id']}
        close_session(h, a, session, (wa, wb))

        # A newcomer restarts the wait: a and b have queued longer than WAIT in
        # total but must still not launch; reaching the target launches at once.
        submit(h, a, wa); submit(h, b, wb)
        time.sleep(WAIT - 0.8)
        submit(h, c, wc)
        quiet(wa, WAIT - 0.8)
        submit(h, d, wd)
        session = offer(wa)
        assert all(offer(ws) == session for ws in (wb, wc, wd))
        assert session_size(h, d, session) == 4
        close_session(h, a, session, (wa, wb, wc, wd))

        # A lone player never launches.
        submit(h, e, we)
        quiet(we, WAIT + 1)
        print('PASS dynamic match size: target launches at once, short group launches after quiet wait,')
        print('     arrivals restart the wait, capacity equals group size, single player never launches')
    finally:
        h.close()

    h = Harness(extra_env={'Matchmaking__Rulesets__test': '3', 'Matchmaking__TicketLifetimeSeconds': '4'})
    try:
        a, b = h.account(), h.account()
        wa, wb = h.connect(a), h.connect(b)
        submit(h, a, wa); submit(h, b, wb)
        event(wa, 'tickets:timedOut'); event(wb, 'tickets:timedOut')
        print('PASS disabled wait keeps exact-count matching')
    finally:
        h.close()

    for bad in ({'Matchmaking__PartialMatchWaitSeconds': '-1'},
                {'Matchmaking__PartialMatchWaitSeconds': '30', 'Matchmaking__TicketLifetimeSeconds': '30'}):
        try:
            Harness(extra_env=bad).close()
        except (AssertionError, OSError):
            continue
        raise AssertionError(f'invalid configuration accepted: {bad}')
    print('PASS invalid wait configuration rejected at startup')


if __name__ == '__main__':
    main()
