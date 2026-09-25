# ReVerse Community Backend

An unofficial, experimental, self-hosted community server backend and desktop
relay for Resident Evil Re:Verse. It is intended to let players connect their
existing game installations to a community-hosted server and play together
over a private ZeroTier network.

⚠️ Disclaimer
This software is an open-source project developed for the community and is not affiliated with any organization or institution.
It is shared purely for educational purposes, software development testing, and to contribute to the growth of the open-source community.

## Keywords and topics

Resident Evil Re:Verse community server, unofficial backend, self-hosted game
server, multiplayer matchmaking, local game relay, private server, ZeroTier
virtual LAN, Windows gaming, Linux gaming, Steam Deck, .NET, Avalonia UI,
signaling, persistent game saves, and experimental protocol compatibility.

## Components

- **Backend:** account management, save-storage APIs, matchmaking, and a
  signaling and packet-relay service for multiplayer connections.
- **Desktop relay:** a cross-platform Windows or Linux application built with
  Avalonia that forwards game service requests to a backend address supplied by
  the player.
- **Tests:** automated checks for backend, relay, and protocol behavior.

The backend is designed for a small private group rather than a public hosted
service. The host runs the backend, each player runs one local relay, and the
relays communicate with the host through the ZeroTier network.

The project does not include the game, game assets, or a license to use them.
Players must obtain their own lawful copy and any required permissions.
Compatibility is experimental; bugs, incomplete features, and connection issues
may occur.

## Quick starts

- [Host quick start](QUICKSTART-HOST.md)
- [Player quick start](QUICKSTART-PLAYER.md)
- [Complete setup guide](SETUP.md)

## AI-assisted help

[![Ask ChatGPT](https://img.shields.io/badge/Ask%20ChatGPT-10A37F?style=for-the-badge&logo=openai&logoColor=white)](https://chatgpt.com/?q=Help%20me%20set%20up%20or%20troubleshoot%20the%20ReVerse%20Community%20Backend.%20Use%20this%20GitHub%20repository%20as%20context%2C%20especially%20README.md%2C%20SETUP.md%2C%20QUICKSTART-HOST.md%2C%20and%20QUICKSTART-PLAYER.md%3A%20https%3A%2F%2Fgithub.com%2Flannahirave%2Freverse-fanmade)
[![Ask Claude](https://img.shields.io/badge/Ask%20Claude-D97757?style=for-the-badge&logo=claude&logoColor=white)](https://claude.ai/new?q=Help%20me%20set%20up%20or%20troubleshoot%20the%20ReVerse%20Community%20Backend.%20Use%20this%20GitHub%20repository%20as%20context%2C%20especially%20README.md%2C%20SETUP.md%2C%20QUICKSTART-HOST.md%2C%20and%20QUICKSTART-PLAYER.md%3A%20https%3A%2F%2Fgithub.com%2Flannahirave%2Freverse-fanmade)

The badges open the selected provider with a prefilled request containing this
repository's URL. If the provider cannot access the repository, paste the
relevant guide into the conversation. Never share secret keys, credentials,
private URLs, or unsanitized logs with anyone.

## Running the packages

The backend and relay packages include their .NET runtimes; no .NET installation
is required on the target machine. The backend is published for Windows x64 and
Linux x64. The relay is published for Windows x64 and Linux x64, including
Steam Deck desktop mode. Extract the appropriate package ZIP and follow the
[setup guide](SETUP.md). Run `Relay.Avalonia.exe` on Windows or
`Relay.Avalonia` on Linux. If Linux does not preserve the executable bit, run
`chmod +x Relay.Avalonia` once. The source checkout still requires the .NET
SDK.

The manually triggered GitHub Actions workflow builds the four self-contained
packages and attaches them to a tagged GitHub release:

Download the current packages from the [ReVerse v0.2.0 release](https://github.com/lannahirave/reverse-fanmade/releases/tag/v0.2.0).

- `ReVerse-backend-win-x64.zip`
- `ReVerse-backend-linux-x64.zip`
- `ReVerseRelay-win-x64.zip`
- `ReVerseRelay-linux-x64.zip`

## Detailed traffic logs

Click **DETAILED LOGS: OFF** in the desktop relay to turn on full traffic
logging. The switch takes effect while the relay is running. If the same app
hosts the backend, it also enables backend logging without restarting it.
Click the button again to stop new detailed entries. The choice is saved for
the next app launch.

The relay writes `detailed-traffic-YYYY-MM-DD.jsonl` in a `detailed-logs`
folder beside the Avalonia executable. A backend hosted from the app writes
the same filename under its `logs` folder by default. The app shows both
locations when detailed logging is on. Each entry has a UTC timestamp and
records its traffic direction. HTTP entries include the full URL, addresses,
ports, headers, and body chunks. WebSocket and signaling entries include full
payload bytes as Base64, with readable UTF-8 alongside them when valid.
These files include credentials and other private data without redaction.

## License and ownership

Original project code is provided under the MIT License, without warranty.
See [LICENSE](LICENSE) for the full terms and third-party rights notice.

Resident Evil / Biohazard and related intellectual property belong to CAPCOM
CO., LTD. and/or their respective rights holders. This project is independent
and is not affiliated with, endorsed by, or approved by CAPCOM or Valve.
