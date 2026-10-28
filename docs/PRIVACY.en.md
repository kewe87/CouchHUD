# Privacy notice

[Deutsch](PRIVACY.md) | **English**

*As of October 2026, applies to CouchHUD from version 0.3*

> This is a translation. In case of doubt, the German version ([PRIVACY.md](PRIVACY.md)) prevails.

CouchHUD is a local application. There is **no account, no telemetry, no usage statistics and no advertising**. The developers of CouchHUD receive no data from you.

## What is stored locally

| Location | Content |
|---|---|
| `%APPDATA%\CouchHud` | Settings (`config.json`: favorites, collections, play times, your own entries, bookmarks, parental control rules), the parental controls' daily counter (`playtime.json`), save game backups and the downloaded save game manifest, caches for artwork, store info and the catalog, the backup of the Windows values CouchHUD changed (`guard.json`) and of the changed Steam settings (`guard-steam.json`; the settings themselves are in Steam's file `userdata\<account>\config\localconfig.vdf` and in the registry value `HKCU\Software\Valve\Steam\StartupMode`; none of it leaves the PC and nothing on the Steam account is changed), installed plugins and skins |
| `%LOCALAPPDATA%\CouchHud` | Logs (`logs`), image cache for artwork (`artwork`, about 750 MB at most; the images of current entries are always kept), the interface profile (WebView2), the browser profile (history, cookies, logins of the websites you visit) |

API keys and tokens (SteamGridDB, Anthropic, OpenAI, MCP and the access of plugins) are stored encrypted with Windows DPAPI for your user account. The couch lock's controller code is only stored as a salted hash. CouchHUD never stores your Windows password, neither in the configuration nor in the log. Only if you set up automatic sign-in do you enter it once in CouchHUD: it is checked with Windows and stored by Windows in encrypted form as an LSA secret (as with Sysinternals Autologon); CouchHUD does not keep it.

You can delete everything by removing both folders.

## Which connections CouchHUD makes

CouchHUD only connects to these services, and only for the purpose given. As with any website visit, the providers see your IP address.

| Service | When | What is sent |
|---|---|---|
| Steam Store (Valve; store.steampowered.com) | Description of your games (when focused, then stored on the PC) and the artwork search in the background | App IDs of your Steam games. For games and apps without a Steam ID, the **name** is searched in the Steam store (search term); each name at most once every 90 days (match) or 7 days (no match). The description is requested in the interface language (German or English, with the matching country code). At most one request at a time, at least 1.5 seconds apart |
| Steam CDN (Valve; shared.cloudflare.steamstatic.com, video.*.steamstatic.com) | Covers, banners, logos (once per image, then stored locally), screenshots and trailers (not stored) | Only the address of the image or video. If Steam has the images locally (`appcache\librarycache`), they are taken from there and not downloaded |
| Epic CDN (cdn1.epicgames.com and others) | Covers and banners of your Epic games, once per image | Only the address of the image. The addresses come from the Epic Launcher's catalog cache on your PC; there is no request to an Epic API and no sign-in |
| SteamGridDB (www.steamgriddb.com and its image CDN) | Only with your own API key, in the background and in the "Change artwork" dialog | Names or IDs of your games and apps, to find artwork. At most one request at a time, at least 0.6 seconds apart |
| GitHub: self-update (api.github.com, github.com) | Every 12 hours and on "Check for updates", never during a game; can be switched off under Settings › Updates | Query for the latest version of CouchHUD (ETag); on "Install", the download of the setup, the checksum file and the signature. No data about you is sent |
| GitHub: plugin catalog (raw.githubusercontent.com, default `kewe87/CouchHUD-Catalog`) | When you open the catalog and in the background (after start-up and every 12 hours, never during a game); can be switched off | Download of `index.json` and `index.json.sig`. When installing, the download of the plugin or skin from the https address given in the index (usually GitHub). You can add further catalog sources yourself |
| GitHub (raw.githubusercontent.com, Ludusavi manifest) | When you first open a game with save games, then at most once a week | Only the download of the public database (~18 MB) with a version tag (ETag); your games are not sent, the matching happens locally |
| Open-Meteo | Only if you set a weather location | The search term for the location and its approximate coordinates |
| RSS feeds (preconfigured: WinFuture, Caschys Blog) | News tab | Download of the feeds and images |
| Home Assistant, through the Home Assistant plugin | Only if the plugin is installed and set up | Requests to your own server |
| TV (LG webOS) in your home network | Only when a TV is paired or searched for: search (SSDP multicast in the local network, 5 seconds at most), pairing, control, switching on (a Wake-on-LAN packet to the TV's MAC address, only into the local network) | Control commands (volume, input, on/off) and status queries straight to the TV, only to private or link-local addresses. Stored on the PC: address, name, model, MAC address, certificate fingerprint and the pairing key (encrypted with DPAPI for your Windows account). No LG cloud, no account. *Remove TV* deletes this again; only the list of addresses and MAC addresses that were already paired with encryption (32 at most, protection against unencrypted pairing) stays in `config.json` (key `tv.secureSeen`) |
| CaseHUD (local API, 127.0.0.1), through the CaseHUD plugin | Only if the plugin is installed and CaseHUD is installed and running; while the plugin's page is open or a game is running | Control commands and status queries to the local CaseHUD (port and token are in its `api.json`), plus the name and process ID of the running game so the displays can switch to the game scene (can be switched off in Expert mode). Does not leave the PC |
| GitHub: CaseHUD release (api.github.com, github.com, `kewe87/CaseHUD`), through the CaseHUD plugin | Only while CaseHUD is not installed yet and you press *Install* in the plugin (no background lookup, none for updating: CaseHUD does that itself) | Query for the latest version; then the download of the setup, `SHA256SUMS.txt` and `SHA256SUMS.txt.sig`. The signature is checked with the built-in CaseHUD release key (SHA-256 fingerprint `cfb6863f23c892a53c93786dd1ee78478618aed680c5603fbc3955c1dde4abaf`); without a valid signature and checksums nothing is installed. No data about you is sent |
| Anthropic or OpenAI | Only with your own API key and only when you use the assistant | Your questions and the information the assistant reads from CouchHUD to answer them (e.g. names and play times of your games, settings). The provider's own privacy policy applies. |
| Microsoft (Windows Update), winget sources | Checking for and installing updates | As usual for Windows Update or winget |
| Microsoft (www.microsoft.com) | Only with parental controls on, when CouchHUD notices a jump of the system clock | One HTTPS request (HEAD), used only for the date in the response, to check the clock. No data is sent |
| Microsoft (WebView2 runtime) | Only during installation, if the runtime is missing | The setup includes the WebView2 bootstrapper, which downloads the runtime from Microsoft. Later, Windows updates the runtime itself |
| Websites in the browser | Only what you open (also links from news and plugins: they open in the built-in browser, not in the Windows default browser) | As in any browser, in its own profile (history, cookies); searches go to DuckDuckGo. Website entries for streaming with copy protection open in Edge if you choose so; that data stays with Edge |
| Game clients (Steam, Epic, …) | CouchHUD only starts them, the clients connect by themselves | Up to the respective providers |
| VPN app (NordVPN), through the VPN plugin | Only when the plugin is installed and you connect or disconnect on the VPN page or in the Guide menu | Neither CouchHUD nor the plugin makes a connection: the plugin calls the command line of the installed app, and the app connects to its provider. The plugin reads the state from the PC's network adapter, without asking third parties (no lookup of the public IP); it has no network right and no credentials. Only the last chosen country and the switch "VPN in the Guide menu" are stored (in the plugin's settings; before the plugin `vpnLastCountry` and `vpnInGuide` in the program, which carry over once) |

## Artwork: stored locally, only in the background, only with consideration

- Each image is downloaded **once** and then kept in `%LOCALAPPDATA%\CouchHud\artwork`. The interface never loads artwork from the internet itself, only from there. Addresses found and failures (7 days) are stored as well.
- Images are only loaded in the background after the library has been read, **never while a game is running**. There is a rate limit per host; on "too many requests" (HTTP 429/503) the server's wait time (Retry-After) is respected, followed by exponential back-off. If a host keeps answering with errors, CouchHUD pauses it for several minutes up to an hour.
- Requests carry the name `CouchHUD/<version>` as the user agent.
- Only complete png, jpeg, webp and gif files up to 12 MB are accepted; the content is checked, not what the server claims.
- Local sources come before any request: Steam's `librarycache`, the Epic Launcher's catalog cache, package logos of Microsoft Store games, icons of the program files. If there is no artwork at all, CouchHUD draws a banner itself (name, color), without the network.
- Under Settings › Library (Expert mode) you can switch off "Look up artwork online" (then only existing images are used) and clear the artwork cache. The switch only applies to images: descriptions and age ratings from the Steam store (needed for parental controls) are still loaded and stored when focused, and trailers can be switched off separately under Look.
- Downloads only come from the hosts named above (Steam, Epic, SteamGridDB and their CDNs); every redirect is checked, at most 3. Connections to addresses in your own network or on this PC are refused, even if a name points there. For this reason CouchHUD does not use the Windows proxy for artwork and store queries.

## Services in your home network

- **Phone remote** (off by default): a small web server on port 8766 in the local network. Only devices paired via QR code may type and control.
- **MCP server** (off by default): port 8765, only on this PC or, if you choose, in the home network, with a token. Every change has to be confirmed on the TV.

## Notifications

If you switch on "Mirror notifications" and Windows grants access, CouchHUD reads the Windows notifications (e.g. WhatsApp, Discord) to show them on the TV. They stay on the PC and are not stored.

## Plugins

Third-party plugins can make their own connections and store their own data. The respective plugin authors are responsible for that.
