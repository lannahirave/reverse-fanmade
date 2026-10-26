# ReVerse community setup

This project provides a small local relay for each player and a community
backend that the host runs for the group. The game connects to the local relay
on `127.0.0.1:5080`; the relay connects to the host through ZeroTier.

## Components

| Component | Windows package | Linux / Steam Deck package |
| --- | --- | --- |
| Backend | `ReVerse-backend-win-x64.zip` | `ReVerse-backend-linux-x64.zip` |
| Player relay | `ReVerseRelay-win-x64.zip` | `ReVerseRelay-linux-x64.zip` |

The published packages are self-contained, so a player does not need to
install .NET. Extract the entire package before running it. Source builds
require the .NET 10 SDK.

The Avalonia relay runs on Windows and Linux. Click **Host backend** to expand
its hosting controls. Windows hosts select the folder containing
`ReVerse.Capture.exe`; Linux and Steam Deck hosts select the folder containing
`ReVerse.Capture`. The app detects the ZeroTier address and starts or stops the
backend for you.

## Get the packages

Packages are built by the repository's manually started GitHub Actions
workflow. Download the Windows or Linux artifacts that match the machine:

Download the current packages from the [ReVerse v0.2.0 release](https://github.com/lannahirave/reverse-fanmade/releases/tag/v0.2.0).

- Windows backend: `ReVerse-backend-win-x64.zip`
- Linux backend: `ReVerse-backend-linux-x64.zip`
- Windows relay: `ReVerseRelay-win-x64.zip`
- Linux relay: `ReVerseRelay-linux-x64.zip`

Do not run an executable directly from inside the ZIP file.

## ZeroTier network

1. Install ZeroTier on the host and every player computer.
2. Create or choose one private ZeroTier network.
3. Give every player the network ID.
4. Authorize every member in the ZeroTier controller.
5. Find the host's managed IPv4 address.

On Windows, run `ipconfig` and use the IPv4 address under the adapter whose
name contains `ZeroTier`. On Linux, use the address shown by the ZeroTier
client or inspect the managed interface with `ip -4 addr`.

The address normally looks like `10.x.x.x`. Call it `HOST_ZT_IP` below. A
player receives this address as part of the Player connection URL;
`127.0.0.1` is only for the game-to-relay connection on that same computer.

## Network ports

The host must allow these inbound ports on the ZeroTier interface:

| Port | Protocol | Purpose |
| --- | --- | --- |
| 6080 | TCP | Backend HTTP API |
| 5070 | UDP | Signaling |

On Windows, run PowerShell as administrator:

```powershell
New-NetFirewallRule -DisplayName "ReVerse backend HTTP" -Direction Inbound -Protocol TCP -LocalPort 6080 -Action Allow
New-NetFirewallRule -DisplayName "ReVerse signaling UDP" -Direction Inbound -Protocol UDP -LocalPort 5070 -Action Allow
```

On Linux or Steam Deck, allow TCP `6080` and UDP `5070` in the active firewall.
The exact firewall command depends on the distribution.

## Start the backend on Windows

### Through the Avalonia app

1. Extract the backend package to a folder.
2. Extract and start `Relay.Avalonia.exe` from the relay package.
3. Click **Host backend** in the upper-right corner to expand the window.
4. Click **Browse** and select the folder containing `ReVerse.Capture.exe`.
5. Click **Detect** and verify the ZeroTier IPv4 address.
6. Set **Players** to the intended group size, from 2 through 10.
   **Fill wait** (default 15 seconds) lets a smaller group of at least 2 start
   once nobody has joined or left the search for that many seconds; set it to
   0 to always wait for the full count.
   The generated command is available under **Advanced command** when
   troubleshooting.
   Optionally click **Generate** next to **Server API key** to require a key
   from every player; see [Server API key](#server-api-key).
7. Click **Start backend**. A separate command window opens, and the app fills
   **Backend server** and **Server API key** in the host's relay section automatically.
8. Use **Stop backend** in the app to close the backend process when finished.

The app displays and can copy the **Player connection URL**, for example:

```text
http://10.205.138.26:6080
```

The host's relay receives that URL automatically. Give it to every remote player.

### Direct launcher

The released Windows backend package contains `ReVerse.Capture.exe`. The relay
app is the recommended launcher. To start it manually, open PowerShell in its
folder, replace the example address, and run:

```powershell
$env:Relay__Enabled="true"
$env:Steam__Mode="fallback"
$env:Matchmaking__ExperimentalSessionProtocol="true"
$env:Matchmaking__Rulesets__match_master="2"
$env:Matchmaking__IgnorePlayerAttributes="true"
$env:ASPNETCORE_URLS="http://10.205.138.26:6080"
$env:Signaling__Enabled="true"
$env:Signaling__PeerDiagnostics="true"
$env:Signaling__BindAddress="0.0.0.0"
$env:Signaling__PublicHost="10.205.138.26"
$env:Signaling__Port="5070"
$env:Signaling__PublicPort="5070"
$env:Signaling__ExperimentalReplies="true"
$env:Signaling__Reply19="1"
$env:Signaling__Reply21="1"
$env:Signaling__RegistrationFieldBIsPeerNumber="true"
$env:Access__ApiKey="replace-with-a-long-random-key"
.\ReVerse.Capture.exe
```

Omit `Access__ApiKey` to run without a server API key.

Change `match_master` to the intended group size from 2 through 10. If binding
to the managed address fails, set `ASPNETCORE_URLS` to
`http://0.0.0.0:6080`; keep `Signaling__PublicHost` set to the ZeroTier address.

## Start the backend on Linux or Steam Deck

### Through the Avalonia app

1. Extract the backend and relay packages.
2. Start `Relay.Avalonia` from the relay package.
3. Click **Host backend** to expand the window, then click **Browse** and select
   the folder containing `ReVerse.Capture`.
4. Click **Detect** and verify the ZeroTier IPv4 address.
5. Set **Players** to the intended group size, from 2 through 10.
   **Fill wait** (default 15 seconds) lets a smaller group of at least 2 start
   once nobody has joined or left the search for that many seconds; set it to
   0 to always wait for the full count.
   The generated command is available under **Advanced command**.
   Optionally click **Generate** next to **Server API key**.
6. Click **Start backend**. The app makes `ReVerse.Capture` executable when
   necessary, starts the backend process, and fills the
   local **Backend server** field with the generated Player connection URL.
7. Copy the **Player connection URL** and give it to the other players. Use
   **Stop backend** in the app when the session is over.

The backend logs remain in the backend folder's `logs` directory. If the app
cannot change the file permission, run `chmod +x ReVerse.Capture` once from the
selected folder and start the backend again.

### Manual fallback

The Linux backend package contains the self-contained `ReVerse.Capture`
executable. Start it from a terminal in the extracted backend folder.

Replace the example address, then start the backend:

```bash
HOST_ZT_IP=10.205.138.26
export Relay__Enabled=true
export Steam__Mode=fallback
export Matchmaking__ExperimentalSessionProtocol=true
export Matchmaking__Rulesets__match_master=2
export Matchmaking__IgnorePlayerAttributes=true
export ASPNETCORE_URLS="http://${HOST_ZT_IP}:6080"
export Signaling__Enabled=true
export Signaling__PeerDiagnostics=true
export Signaling__BindAddress=0.0.0.0
export Signaling__PublicHost="${HOST_ZT_IP}"
export Signaling__Port=5070
export Signaling__PublicPort=5070
export Signaling__ExperimentalReplies=true
export Signaling__Reply19=1
export Signaling__Reply21=1
export Signaling__RegistrationFieldBIsPeerNumber=true
./ReVerse.Capture
```

Change `Matchmaking__Rulesets__match_master=2` to the desired size from 2
through 10. If the backend cannot bind to the managed address, set
`ASPNETCORE_URLS="http://0.0.0.0:6080"` and leave
`Signaling__PublicHost` set to the ZeroTier address.

Stop the backend with `Ctrl+C` in its terminal.

## Start a relay

Run one relay on every computer, including the host:

- Windows: run `Relay.Avalonia.exe`.
- Linux or Steam Deck:

  ```bash
  chmod +x Relay.Avalonia
  ./Relay.Avalonia
  ```

Fill in:

1. **Username** — the name for this player.
2. **Backend server** — the Player connection URL supplied by the host, such as
   `http://10.205.138.26:6080`.
3. **Server API key (optional)** — the key supplied by the host, if the host set
   one. Leave it empty otherwise. It is not saved between runs.

The relay creates its internal connection value automatically; there is no
secret-key field to complete.

Click **Start relay** and keep the app running. The relay listens locally for
the game on port `5080`; this port should not be exposed to the network.

## Server API key

The host can require a shared key. When `Access__ApiKey` is set, the backend
answers any HTTP request or WebSocket connection that does not carry the key in
the `x-api-key` header with `401 Unauthorized`. The relay sends the key on every
request.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Access__ApiKey` | empty | Shared key, 16–1024 characters; empty disables the check |

The relay app accepts keys of 16–128 letters, digits, dots, dashes and
underscores. Use HTTPS when the backend is reachable from the internet, so the
key is not sent in clear text. The UDP signaling port is not covered by the key.

## Configure the game

Click **Copy** beside **Steam launch option** in the relay app and paste the value
into the game's Steam launch options:

```text
/Rebe/HjmUriStr:http://127.0.0.1:5080
```

This option is the same on every computer. Do not replace `127.0.0.1` with the
host's ZeroTier address: the game must talk to its own local relay.

Start the relay before opening the game. Have all players search at roughly
the same time.

## Connection checks

From a Windows player:

```powershell
ping 10.205.138.26
Test-NetConnection 10.205.138.26 -Port 6080
```

From Linux or Steam Deck:

```bash
ping 10.205.138.26
nc -vz 10.205.138.26 6080
```

Replace the address with the host's actual ZeroTier address. A failed TCP
check means the Player connection URL, backend process, ZeroTier authorization, binding,
or host firewall needs attention. A successful TCP check does not replace the
UDP `5070` firewall rule required for signaling.

## Troubleshooting

### The host app cannot start the backend

The selected backend folder must contain `ReVerse.Capture.exe` on Windows or
`ReVerse.Capture` on Linux. Re-extract the backend ZIP if that file is missing.
Open **Advanced command** to inspect the generated settings before clicking
**Start backend**.

### ZeroTier address is not detected

On Windows, click **Detect** after ZeroTier is connected. The adapter name must
contain `ZeroTier`. You can enter the managed IPv4 address manually if the
adapter has an unusual name.

### A remote player cannot connect

Confirm that the player is authorized in ZeroTier and is using the host's
Player connection URL on TCP `6080`. Check the host firewall and verify that the backend
terminal is still running. Do not use `localhost` or `127.0.0.1` as a remote
player's backend address.

### Everyone remains in “searching for players”

Confirm that every relay is running, every game uses the local launch option,
all players use the same Player connection URL, and the backend **Players** value matches
the intended group size, or that **Fill wait** is above 0 so a smaller group can start. After a backend restart, stop and restart the relays
and games before searching again so stale sessions are not reused.

### Where to look for backend evidence

The backend writes request logs under its `logs` directory, including files
named like `requests-YYYY-MM-DD.jsonl`. The backend terminal also shows startup,
matchmaking, signaling, and error messages. Keep a copy of the relevant log
period when reporting a failed search.

## Legal notice

This is an unofficial community project. Use it at your own risk. See
[LICENSE](LICENSE) for the project's disclaimer and third-party rights notice.
