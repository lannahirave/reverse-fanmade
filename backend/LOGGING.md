# File logging

Request and detailed-traffic JSONL records use Serilog. Files roll daily and at 1,000,000 bytes (1 MB). Serilog finishes the current record before rolling, so a file can exceed the threshold by one complete record. Oversized records remain intact.

Files use `requests-YYYYMMDD.jsonl` and `detailed-traffic-YYYYMMDD.jsonl`, with numbered suffixes when a file rolls by size. Existing files are retained; the limit applies to each file, not total disk usage.

Records retain their existing JSON structure and are flushed after each write. File handles are reused and released at shutdown. Detailed logging remains opt-in through the existing control file or toggle. The relay uses the same detailed-traffic file policy.

File-write failures are reported without failing requests or notification delivery. Records that cannot be written are not retained in memory or retried. Console messages describe startup, sign-ins, player connections, searches, lobby changes and shutdown. Routine route traces and protocol settings stay in files. Console errors omit stack traces.

`backend-YYYYMMDD.jsonl` stores structured application diagnostics, request durations, event properties and full exception details. It is always enabled and uses the same daily and 1 MB rolling policy. `requests-*.jsonl` keeps the existing request records; `detailed-traffic-*.jsonl` remains opt-in for full traffic recording. A failed backend log destination produces a console warning directly.
