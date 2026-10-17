# Host quick start

This guide is for the player who hosts the community backend. The backend is
reachable through the host's ZeroTier address; every player runs their own
local relay and points it at that address.

Download the backend and relay packages from the [ReVerse v0.2.0 release](https://github.com/lannahirave/reverse-fanmade/releases/tag/v0.2.0).

## 1. Join the ZeroTier network

Install ZeroTier on every computer, join the same network, and authorize every
member in the ZeroTier controller. Give the other players the network ID.

Find the host's managed IPv4 address. On Windows, open a terminal and run:

```text
ipconfig
```

Use the IPv4 address under the adapter whose name contains `ZeroTier`. It will
usually look like `10.x.x.x`. Do not give players `127.0.0.1` or a normal
home-LAN address.

## 2. Open the host ports

On Windows, run PowerShell as administrator on the host:

```powershell
New-NetFirewallRule -DisplayName "ReVerse backend HTTP" -Direction Inbound -Protocol TCP -LocalPort 6080 -Action Allow
New-NetFirewallRule -DisplayName "ReVerse signaling UDP" -Direction Inbound -Protocol UDP -LocalPort 5070 -Action Allow
```

On Linux or Steam Deck, allow TCP `6080` and UDP `5070` in the firewall used by
the host. ZeroTier must also be allowed to communicate on the managed network.

## 3. Start the backend on Windows

1. Download and extract both the backend and relay packages.
2. Start `Relay.Avalonia.exe` from the relay package.
3. Click **Host backend** in the upper-right corner to expand the window.
4. In **Backend host**, choose the folder containing `ReVerse.Capture.exe`.
5. Click **Detect** next to the ZeroTier address and check that the detected
   address is the host's managed address.
6. Set **Players** to the intended group size, from 2 through 10.
   **Fill wait** (default 15 seconds) lets a smaller group of at least 2 start
   once nobody has joined or left the search for that many seconds; set it to
   0 to always wait for the full count.
   The generated command is available under **Advanced command** when
   troubleshooting.
7. Click **Start backend**. It opens the backend in a separate command window
   and automatically fills **Backend server** in the host's relay section.
   Use **Stop backend** in the relay app when the session is over.
8. Copy the **Player connection URL**. It should be similar to:

   ```text
   http://10.205.138.26:6080
   ```

The backend folder must contain the supplied `ReVerse.Capture.exe`. If an
unusual option is required, expand **Advanced command** before starting it.

## 4. Start the host's relay

In the same Avalonia app, fill in:

- **Username**: the name this player will use.
- **Backend server**: this is filled automatically when the backend starts.
  Verify that it matches the Player connection URL.

Click **Start relay**. Copy the launch option shown in the app:

```text
/Rebe/HjmUriStr:http://127.0.0.1:5080
```

Add that value to the game's Steam launch options before starting the game.

## 5. Give players the connection details

Send each player:

1. The ZeroTier network ID.
2. The copied **Player connection URL**, such as `http://10.205.138.26:6080`.
3. The launch option shown above.

Each player chooses their own username. No secret key needs to be exchanged.

## 6. Linux or Steam Deck host

The backend and Avalonia relay packages include their .NET runtime. The
Avalonia host panel also works on Linux and Steam Deck.

1. Extract the backend and relay packages.
2. Start `Relay.Avalonia`.
3. Click **Host backend** to expand the window, then click **Browse** and select
   the folder containing
   `ReVerse.Capture`.
4. Click **Detect** and verify the ZeroTier address.
5. Set **Players** to the desired match size from 2 through 10.
   **Fill wait** (default 15 seconds) lets a smaller group of at least 2 start
   once nobody has joined or left the search for that many seconds; set it to
   0 to always wait for the full count.
   The generated command is available under **Advanced command**.
6. Click **Start backend**. The app makes the executable runnable when
   necessary, starts the backend, and fills the local
   **Backend server** field automatically.
7. Copy the displayed **Player connection URL** and give it to the other players.
8. Use **Stop backend** in the app when finished.

The long terminal form remains available if you do not want to use the host
panel. Replace `10.205.138.26` below with the host's ZeroTier IPv4 address,
then run:

```bash
export Relay__Enabled=true
export Steam__Mode=fallback
export Matchmaking__ExperimentalSessionProtocol=true
export Matchmaking__Rulesets__match_master=2
export Matchmaking__IgnorePlayerAttributes=true
export ASPNETCORE_URLS=http://10.205.138.26:6080
export Signaling__Enabled=true
export Signaling__PeerDiagnostics=true
export Signaling__BindAddress=0.0.0.0
export Signaling__PublicHost=10.205.138.26
export Signaling__Port=5070
export Signaling__PublicPort=5070
export Signaling__ExperimentalReplies=true
export Signaling__Reply19=1
export Signaling__Reply21=1
export Signaling__RegistrationFieldBIsPeerNumber=true
./ReVerse.Capture
```

Change `Matchmaking__Rulesets__match_master=2` to a value from 2 through 10
when a different match size is needed. Add
`Matchmaking__PartialMatchWaitSeconds=15` to let a smaller group of at least
2 start after the search has been unchanged for 15 seconds. If binding to the managed address
fails, use `ASPNETCORE_URLS=http://0.0.0.0:6080`; keep
`Signaling__PublicHost` set to the ZeroTier address.

The Linux relay is started with:

```bash
chmod +x Relay.Avalonia
./Relay.Avalonia
```

Enter the same Player connection URL in its relay panel and use the same game launch
option.

## 7. Start together

Start the backend first, then all relays, then launch the game on every
computer with the local relay option. Have everyone search at roughly the
same time. Keep the backend and relay windows open while playing.

For remote players, the Player connection URL is the host's ZeroTier address on port
6080. The game launch option always points to the player's own computer at
`127.0.0.1:5080`.
