# File logging

Request and detailed-traffic JSONL records use Serilog. Files roll daily and at 1,000,000 bytes (1 MB). Serilog finishes the current record before rolling, so a file can exceed the threshold by one complete record. Oversized records remain intact.

Files use `requests-YYYYMMDD.jsonl` and `detailed-traffic-YYYYMMDD.jsonl`, with numbered suffixes when a file rolls by size. Existing files are retained; the limit applies to each file, not total disk usage.

Records retain their existing JSON structure and are flushed after each write. File handles are reused and released at shutdown. Detailed logging remains opt-in through the existing control file or toggle. The relay uses the same detailed-traffic file policy.

File-write failures are reported without failing requests or notification delivery. Records that cannot be written are not retained in memory or retried. Console messages describe startup, sign-ins, player connections, searches, lobby changes and shutdown. Routine route traces and protocol settings stay in files. Console errors omit stack traces.

`backend-YYYYMMDD.jsonl` stores structured application diagnostics, request durations, event properties and full exception details. It is always enabled and uses the same daily and 1 MB rolling policy. `requests-*.jsonl` keeps the existing request records; `detailed-traffic-*.jsonl` remains opt-in for full traffic recording. A failed backend log destination produces a console warning directly.

## UDP conversations

UDP logging is disabled by default, independently of full HTTP/traffic recording. Explicitly set `UdpLogging:Enabled` to `true` (environment: `UdpLogging__Enabled=true`) to enable it. The signaling listener must also be enabled for it to receive UDP.

In the Avalonia app, open **Backend host** and select **Allow UDP logging** before starting the backend. The choice is saved for future starts and is independent of the relay detailed-logging control. Stop the backend to change it. The generated command explicitly sets UDP logging on or off.

Two files are written under `Capture:LogDirectory`:

- `udp-YYYYMMDD.log`: explanations, sender/recipient, session/connection IDs, decoded fields and hex dumps.
- `udp-YYYYMMDD.jsonl`: the same events with structured fields, exact Base64/hex payloads and UTF-8 where valid.

Both roll daily and at **10,000,000 bytes (10 MB)**, completing the current event before rotation. Their file numbers can differ because the formats have different sizes. Match records using `eventId`; follow forwarded messages using `forwardingId`. Existing logs retain their 1 MB policy. No automatic file deletion is enabled.

The log records datagrams, authentication-to-endpoint mapping, packet sequences, fragments, assembled messages, duplicate packets, routing decisions, per-recipient queue acceptance, sends, retries, acknowledgments, rejection reasons and closure counts. Outgoing datagrams include their originating wire sequence when available. Incoming datagrams and plaintext link through the endpoint/connection; DTLS framing does not imply a one-to-one packet mapping. An acknowledgment confirms transport receipt, not gameplay processing. Forwarded peer data currently uses an unreliable channel and is not retransmitted by the backend.

Unencrypted DTLS handshake contents, registration credentials, and incoming fragment bytes that could contain split credentials are omitted. Malformed plaintext with unknown credential locations is omitted. Complete non-credential messages and forwarded peer blocks retain their bytes. Known peer fields are decoded; unknown gameplay data is not labeled as positions or actions. Logging does not capture traffic that bypasses the backend. The separate opt-in full-traffic log keeps its existing capture behavior.

Formatting and file writes run on a background worker. `UdpLogging:QueueCapacity` limits pending record count; `UdpLogging:MaxQueuedBytes` limits copied payload bytes plus a 4 KiB allowance per record. Queue overload creates a `logging_gap` event and console warning with dropped-record counts. File failures are reported on the console; records that cannot be written are not retried, and one file may be incomplete independently of the other. Shutdown drains accepted events after network workers stop. Abrupt process termination can lose queued records.
