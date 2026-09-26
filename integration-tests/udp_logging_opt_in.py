import json
import os
import socket
import time

from matchmaking_flow import Harness


def check(setting):
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
        probe.bind(('127.0.0.1', 0))
        port = probe.getsockname()[1]
    environment = {
        'Signaling__Enabled': 'true',
        'Signaling__BindAddress': '127.0.0.1',
        'Signaling__Port': str(port),
        'Signaling__PublicPort': str(port),
        'Signaling__PublicHost': '127.0.0.1',
    }
    if setting is not None:
        environment['UdpLogging__Enabled'] = setting
    harness = Harness(extra_env=environment)
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
            client.sendto(b'udp-opt-in-check', ('127.0.0.1', port))
        deadline = time.monotonic() + (5 if setting == 'true' else 1)
        while time.monotonic() < deadline:
            files = list(harness.folder.glob('udp-*.jsonl'))
            if setting == 'true' and files:
                records = [json.loads(line) for file in files for line in file.read_text().splitlines()]
                if any(record['stage'] == 'datagram_received' for record in records):
                    readable = list(harness.folder.glob('udp-*.log'))
                    if readable and any('datagram_received' in file.read_text() for file in readable):
                        break
            time.sleep(.05)
        if setting == 'true':
            assert files and any(record['stage'] == 'datagram_received' for record in records), harness.folder
            assert readable and any('datagram_received' in file.read_text() for file in readable), harness.folder
        else:
            assert not list(harness.folder.glob('udp-*')), harness.folder
        assert harness.proc.poll() is None, 'Backend stopped unexpectedly'
        print('PASS UDP files ' + ('enabled' if setting == 'true' else 'absent') + ' with setting ' + str(setting))
    finally:
        harness.close()


if __name__ == '__main__':
    os.environ.pop('UdpLogging__Enabled', None)
    for setting in (None, 'true', 'false'):
        check(setting)
