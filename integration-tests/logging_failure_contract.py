"""Logging failures must not interrupt HTTP or notification lifetimes."""
import json
import tempfile
import time
from pathlib import Path

from matchmaking_flow import Harness, event
from private_match_contract import create_body


def check(log_path):
    h = Harness(extra_env={'Capture__LogDirectory': str(log_path)})
    try:
        host = h.account('logging-host')
        for exceptional in (False, True):
            ws = h.connect(host)
            room = h.request('POST', '/v1/gameSession', host, create_body(host))['gameSessions'][0]
            event(ws, 'players:created')
            if exceptional:
                ws.send(json.dumps({'command': 'unknown-command'}))
                try:
                    ws.recv()
                except OSError:
                    pass
            else:
                ws.close(timeout=.1)
            deadline = time.monotonic() + 5
            while True:
                try:
                    h.request('GET', '/v1/gameSession', host,
                              headers={'X-Be-Session-Ids': room['sessionId']}, expected=404)
                    break
                except AssertionError:
                    if time.monotonic() >= deadline:
                        raise
                    time.sleep(.05)
    finally:
        h.close()


def main():
    with tempfile.TemporaryDirectory(prefix='reverse-log-failure-') as directory:
        root = Path(directory)
        check(root / 'working')
        blocked = root / 'blocked'
        blocked.write_text('A file cannot be used as a log directory.', encoding='utf-8')
        check(blocked)
    print('PASS HTTP responses, notification delivery and clean/exceptional cleanup with healthy/failing logging')


if __name__ == '__main__':
    main()
