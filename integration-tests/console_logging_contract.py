"""Console activity and diagnostic files describe the same backend lifecycle."""
import json
import os
import subprocess
import tempfile
import time
from pathlib import Path

from matchmaking_flow import Harness, event, ticket, ROOT
from private_match_contract import create_body


def wait_for(check):
    deadline = time.monotonic() + 5
    while not check():
        assert time.monotonic() < deadline, 'Log output did not arrive'
        time.sleep(.05)


def main():
    h = Harness()
    try:
        account = h.account('console-test')
        ws = h.connect(account)
        request = ticket(account, ruleset='not-configured')
        first = h.request('POST', '/v1/matchmaking/ticket', account, request)
        event(ws, 'tickets:submitted')
        assert h.request('POST', '/v1/matchmaking/ticket', account, request) == first
        h.request('DELETE', '/v1/matchmaking/ticket', account, headers={'X-Be-Ticket-Id': first['ticketId']})
        event(ws, 'tickets:canceled')
        room = h.request('POST', '/v1/gameSession', account, create_body(account))['gameSessions'][0]
        event(ws, 'players:created')
        ws.close(timeout=.1)
        console_path = h.folder / 'backend.log'
        wait_for(lambda: 'Game session closed.' in console_path.read_text(encoding='utf-8'))
        console = console_path.read_text(encoding='utf-8')
        for message in ('Starting the backend', 'Backend ready.', 'Player signed in',
                        'Player notification connection is ready.', 'Match search canceled.',
                        'Private lobby created.', 'Player notification connection closed.'):
            assert message in console, (message, console)
        assert console.count('Player is searching for a match.') == 1, console
        for hidden in ('Dynamic route matched', 'Path override matched', 'Steam sign-in identity:',
                       account['sessionToken'], account['gameKey'], first['ticketId'], room['sessionId']):
            assert hidden not in console, hidden
        records = [json.loads(line) for path in h.folder.glob('backend-*.jsonl')
                   for line in path.read_text(encoding='utf-8').splitlines()]
        assert any(r['MessageTemplate'].startswith('HTTP ') and 'Elapsed' in r['Properties'] for r in records)
        assert any(r['MessageTemplate'].startswith('Dynamic route matched') for r in records)
        assert any(r.get('Properties', {}).get('TicketId') == first['ticketId'] for r in records)
        assert any(r.get('Properties', {}).get('SessionId') == room['sessionId'] for r in records)
    finally:
        h.close()

    with tempfile.TemporaryDirectory(prefix='reverse-startup-log-') as directory:
        env = dict(os.environ, Capture__LogDirectory=directory, Matchmaking__TicketLifetimeSeconds='0')
        exe = os.environ.get('REVERSE_TEST_BACKEND_EXE')
        command = [exe] if exe else ['dotnet', os.environ.get('REVERSE_TEST_BACKEND_DLL',
                                    str(ROOT / 'backend/bin/Debug/net10.0/ReVerse.Capture.dll'))]
        process = subprocess.run(command, cwd=ROOT / 'backend', env=env, capture_output=True,
                                 text=True, timeout=20,
                                 creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        assert process.returncode != 0
        assert 'The backend could not continue.' in process.stdout, process.stdout
        assert 'ArgumentException' not in process.stdout + process.stderr
        records = [json.loads(line) for path in Path(directory).glob('backend-*.jsonl')
                   for line in path.read_text(encoding='utf-8').splitlines()]
        assert any(r['Level'] == 'Fatal' and 'ArgumentException' in r.get('Exception', '') for r in records)
    print('PASS console activity, retry deduplication, file-only diagnostics and startup failure details')


if __name__ == '__main__':
    main()
