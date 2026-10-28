# Datenschutzhinweis

**Deutsch** | [English](PRIVACY.en.md)

*Stand: Oktober 2026, gilt für CouchHUD ab Version 0.3*

CouchHUD ist eine lokale Anwendung. Es gibt **kein Konto, keine Telemetrie, keine Nutzungsstatistik und keine Werbung**. Die Entwickler von CouchHUD erhalten keine Daten von dir.

## Was lokal gespeichert wird

| Ort | Inhalt |
|---|---|
| `%APPDATA%\CouchHud` | Einstellungen (`config.json`: Favoriten, Sammlungen, Spielzeiten, eigene Einträge, Lesezeichen, Jugendschutz-Regeln), Tageszähler des Jugendschutzes (`playtime.json`), Spielstand-Sicherungen und das heruntergeladene Spielstand-Manifest, Caches für Artwork, Store-Infos und Katalog, die Sicherung der von CouchHUD geänderten Windows-Werte (`guard.json`) und der geänderten Steam-Einstellungen (`guard-steam.json`; die Einstellungen selbst stehen in Steams Datei `userdata\<Konto>\config\localconfig.vdf` und im Registry-Wert `HKCU\Software\Valve\Steam\StartupMode`, nichts davon verlässt den PC und nichts am Steam-Konto wird geändert), installierte Plugins und Skins |
| `%LOCALAPPDATA%\CouchHud` | Protokolle (`logs`), Bild-Cache für Artwork (`artwork`, höchstens ca. 750 MB, die Bilder aktueller Einträge bleiben immer erhalten), Profil der Oberfläche (WebView2), Profil des Browsers (Verlauf, Cookies, Logins der besuchten Websites) |

API-Keys und Tokens (SteamGridDB, Anthropic, OpenAI, MCP und die Zugänge der Plugins) werden mit Windows DPAPI für dein Benutzerkonto verschlüsselt gespeichert. Der Controller-Code der Couch-Sperre wird nur als gesalzener Hash gespeichert. Dein Windows-Passwort speichert CouchHUD nie, weder in der Konfiguration noch im Protokoll. Nur wenn du die automatische Anmeldung einrichtest, gibst du es einmal in CouchHUD ein: Es wird mit Windows geprüft und von Windows verschlüsselt als LSA-Secret abgelegt (wie bei Sysinternals Autologon), CouchHUD behält es nicht.

Alles lässt sich löschen, indem du die beiden Ordner entfernst.

## Welche Verbindungen CouchHUD aufbaut

CouchHUD verbindet sich nur mit diesen Diensten, und nur für den genannten Zweck. Dabei sehen die Anbieter, wie bei jedem Webseitenaufruf, deine IP-Adresse.

| Dienst | Wann | Was übertragen wird |
|---|---|---|
| Steam Store (Valve; store.steampowered.com) | Beschreibung deiner Spiele (beim Fokussieren, danach auf dem PC gespeichert) und die Suche nach Artwork im Hintergrund | App-IDs deiner Steam-Spiele. Für Spiele und Apps ohne Steam-ID wird der **Name** im Steam-Store gesucht (Suchbegriff); jeder Name höchstens einmal je 90 Tage (Treffer) bzw. 7 Tage (kein Treffer). Die Beschreibung wird in der Sprache der Oberfläche abgefragt (Deutsch bzw. Englisch, mit passendem Ländercode). Höchstens eine Anfrage gleichzeitig, mindestens 1,5 Sekunden Abstand |
| Steam-CDN (Valve; shared.cloudflare.steamstatic.com, video.*.steamstatic.com) | Cover, Banner, Logos (einmal je Bild, dann lokal gespeichert), Screenshots und Trailer (werden nicht gespeichert) | Nur die Adresse des Bildes bzw. Videos. Wenn Steam die Bilder lokal hat (`appcache\librarycache`), werden sie von dort genommen und nicht geladen |
| Epic-CDN (cdn1.epicgames.com u. a.) | Cover und Banner deiner Epic-Spiele, einmal je Bild | Nur die Adresse des Bildes. Die Adressen stammen aus dem Katalog-Cache des Epic Launchers auf deinem PC, es gibt keine Anfrage an eine Epic-API und keine Anmeldung |
| SteamGridDB (www.steamgriddb.com und dessen Bild-CDN) | Nur mit eigenem API-Key, im Hintergrund und im Dialog „Artwork ändern“ | Namen bzw. IDs deiner Spiele und Apps, um Artwork zu finden. Höchstens eine Anfrage gleichzeitig, mindestens 0,6 Sekunden Abstand |
| GitHub: Self-Update (api.github.com, github.com) | Alle 12 Stunden und bei „Nach Updates suchen“, nie während eines Spiels; abschaltbar unter Einstellungen › Updates | Abfrage der neuesten Version von CouchHUD (ETag), bei „Installieren“ der Download von Setup, Prüfsummen-Datei und Signatur. Es werden keine Daten über dich übertragen |
| GitHub: Plugin-Katalog (raw.githubusercontent.com, Standard `kewe87/CouchHUD-Catalog`) | Beim Öffnen des Katalogs und im Hintergrund (nach dem Start und alle 12 Stunden, nie im Spiel); abschaltbar | Abruf von `index.json` und `index.json.sig`. Beim Installieren der Download des Plugins bzw. Skins von der im Index angegebenen https-Adresse (meist GitHub). Weitere Katalogquellen kannst du selbst eintragen |
| GitHub (raw.githubusercontent.com, Ludusavi-Manifest) | Beim ersten Öffnen eines Spiels mit Spielständen, danach höchstens einmal pro Woche | Nur der Abruf der öffentlichen Datenbank (~18 MB) mit einer Versionsangabe (ETag); deine Spiele werden nicht übertragen, die Zuordnung passiert lokal |
| Open-Meteo | Nur wenn du einen Wetter-Ort festlegst | Suchbegriff des Ortes und dessen ungefähre Koordinaten |
| RSS-Feeds (vorkonfiguriert: WinFuture, Caschys Blog) | News-Tab | Abruf der Feeds und Bilder |
| Home Assistant, über das Home-Assistant-Plugin | Nur wenn das Plugin installiert und eingerichtet ist | Anfragen an deinen eigenen Server |
| Fernseher (LG webOS) im Heimnetz | Nur wenn ein Fernseher gekoppelt bzw. gesucht wird: Suche (SSDP-Multicast im lokalen Netz, höchstens 5 Sekunden), Koppeln, Steuern, Einschalten (Wake-on-LAN-Paket an die MAC-Adresse des Fernsehers, nur ins lokale Netz) | Steuerbefehle (Lautstärke, Eingang, Ein/Aus) und Statusabfragen direkt an den Fernseher, nur an private bzw. link-lokale Adressen. Auf dem PC gespeichert: Adresse, Name, Modell, MAC-Adresse, Zertifikats-Fingerabdruck und der Kopplungsschlüssel (mit DPAPI für dein Windows-Konto verschlüsselt). Keine Cloud von LG, kein Konto. *Fernseher entfernen* löscht das wieder; nur die Liste der Adressen und MAC-Adressen, die schon verschlüsselt gekoppelt waren (höchstens 32, Schutz gegen unverschlüsselte Kopplung), bleibt in `config.json` (Schlüssel `tv.secureSeen`) |
| CaseHUD (lokale API, 127.0.0.1), über das CaseHUD-Plugin | Nur wenn das Plugin installiert ist, CaseHUD installiert ist und läuft; die Seite des Plugins ist geöffnet bzw. ein Spiel läuft | Steuerbefehle und Statusabfragen an das lokale CaseHUD (Port und Token stehen in dessen `api.json`), außerdem Name und Prozess-ID des laufenden Spiels, damit die Displays auf die Spiel-Szene wechseln können (abschaltbar im Expertenmodus). Verlässt den PC nicht |
| GitHub: CaseHUD-Release (api.github.com, github.com, `kewe87/CaseHUD`), über das CaseHUD-Plugin | Nur wenn CaseHUD noch nicht installiert ist und du im Plugin *Installieren* drückst (kein Abruf im Hintergrund, kein Abruf zum Aktualisieren: das macht CaseHUD selbst) | Abfrage der neuesten Version; danach der Download von Setup, `SHA256SUMS.txt` und `SHA256SUMS.txt.sig`. Die Signatur wird mit dem fest eingebauten CaseHUD-Release-Schlüssel (SHA-256-Fingerprint `cfb6863f23c892a53c93786dd1ee78478618aed680c5603fbc3955c1dde4abaf`) geprüft; ohne gültige Signatur und Prüfsummen wird nichts installiert. Es werden keine Daten über dich übertragen |
| Anthropic oder OpenAI | Nur mit eigenem API-Key und nur, wenn du den Assistenten benutzt | Deine Fragen und die Informationen, die der Assistent dafür aus CouchHUD liest (z.B. Namen und Spielzeiten deiner Spiele, Einstellungen). Es gelten die Datenschutzbestimmungen des jeweiligen Anbieters. |
| Microsoft (Windows Update), winget-Quellen | Update-Prüfung und Installation | Wie bei Windows Update bzw. winget üblich |
| Microsoft (www.microsoft.com) | Nur bei eingeschaltetem Jugendschutz, wenn CouchHUD einen Sprung der Systemuhr bemerkt | Eine HTTPS-Anfrage (HEAD) nur auf das Datum in der Antwort, um die Uhr zu prüfen. Es werden keine Daten übertragen |
| Microsoft (WebView2-Runtime) | Nur bei der Installation, wenn die Runtime fehlt | Das Setup bringt den WebView2-Bootstrapper mit, der die Runtime bei Microsoft herunterlädt. Später aktualisiert Windows die Runtime selbst |
| Websites im Browser | Nur was du aufrufst (auch Links aus Nachrichten und Plugins: sie öffnen im eingebauten Browser, nicht im Windows-Standardbrowser) | Wie in jedem Browser, im eigenen Profil (Verlauf, Cookies); Suchen gehen an DuckDuckGo. Website-Einträge für Streaming mit Kopierschutz öffnen auf Wunsch im Edge, dessen Daten bleiben bei Edge |
| Spiele-Clients (Steam, Epic, …) | Startet CouchHUD nur, die Clients verbinden sich selbst | Liegt bei den jeweiligen Anbietern |
| VPN-App (NordVPN), über das VPN-Plugin | Nur wenn das Plugin installiert ist und du auf der VPN-Seite oder im Guide-Menü verbindest oder trennst | Weder CouchHUD noch das Plugin bauen eine Verbindung auf: Das Plugin ruft die Befehlszeile der installierten App auf, die App verbindet sich mit ihrem Anbieter. Den Status liest das Plugin vom Netzwerkadapter des PCs, ohne Anfrage an Dritte (keine Abfrage der öffentlichen IP); es hat kein Netzwerk-Recht und keine Zugangsdaten. Gespeichert wird nur das zuletzt gewählte Land und der Schalter „VPN im Guide-Menü“ (in den Einstellungen des Plugins; vor dem Plugin `vpnLastCountry` und `vpnInGuide` im Programm, die einmal übernommen werden) |

## Artwork: lokal gespeichert, nur im Hintergrund, nur mit Rücksicht

- Jedes Bild wird **einmal** heruntergeladen und liegt danach in `%LOCALAPPDATA%\CouchHud\artwork`. Die Oberfläche lädt Artwork nie selbst aus dem Internet, sondern nur von dort. Gefundene Adressen und Fehlschläge (7 Tage) werden ebenfalls gespeichert.
- Bilder werden nur im Hintergrund nach dem Einlesen der Bibliothek geladen, **nie während ein Spiel läuft**. Pro Host gibt es eine Ratenbegrenzung, bei „zu viele Anfragen“ (HTTP 429/503) wird die Wartezeit des Servers (Retry-After) eingehalten, danach folgt exponentielles Warten. Antwortet ein Host wiederholt mit Fehlern, pausiert CouchHUD ihn für mehrere Minuten bis zu einer Stunde.
- Anfragen tragen den Namen `CouchHUD/<Version>` als User-Agent.
- Akzeptiert werden nur vollständige png-, jpeg-, webp- und gif-Dateien bis 12 MB; geprüft wird der Inhalt, nicht die Angabe des Servers.
- Lokale Quellen kommen vor jeder Anfrage: Steams `librarycache`, der Katalog-Cache des Epic Launchers, Paket-Logos von Microsoft-Store-Spielen, Symbole der Programmdateien. Fehlt Artwork ganz, zeichnet CouchHUD selbst ein Banner (Name, Farbe), ohne Netzwerk.
- Unter Einstellungen › Bibliothek (Expertenmodus) schaltest du „Artwork online suchen“ aus (dann nur noch vorhandene Bilder) und leerst den Artwork-Cache. Der Schalter gilt nur für Bilder: Beschreibungen und Altersfreigaben aus dem Steam-Store (nötig für den Jugendschutz) werden beim Fokussieren weiter geladen und gespeichert, Trailer lassen sich unter Darstellung getrennt abschalten.
- Heruntergeladen wird nur von den oben genannten Hosts (Steam, Epic, SteamGridDB und deren CDNs); jede Weiterleitung wird geprüft, höchstens 3. Verbindungen zu Adressen im eigenen Netz oder auf diesem PC werden abgelehnt, auch wenn ein Name dorthin zeigt. Dafür nutzt CouchHUD für Artwork und Store-Abfragen nicht den Windows-Proxy.

## Dienste im Heimnetz

- **Handy-Fernbedienung** (standardmäßig aus): ein kleiner Webserver auf Port 8766 im lokalen Netz. Nur per QR-Code gekoppelte Geräte dürfen tippen und steuern.
- **MCP-Server** (standardmäßig aus): Port 8765, nur auf diesem PC oder auf Wunsch im Heimnetz, mit Token. Jede Änderung muss am TV bestätigt werden.

## Benachrichtigungen

Wenn du „Benachrichtigungen spiegeln“ einschaltest und Windows den Zugriff erlaubst, liest CouchHUD die Windows-Benachrichtigungen (z.B. WhatsApp, Discord), um sie am TV anzuzeigen. Sie bleiben auf dem PC und werden nicht gespeichert.

## Plugins

Plugins von Dritten können eigene Verbindungen aufbauen und eigene Daten speichern. Dafür sind die jeweiligen Plugin-Autoren verantwortlich.

## Summary in English (artwork)

CouchHUD downloads each cover/banner image once and serves it from `%LOCALAPPDATA%\CouchHud\artwork` afterwards; the UI never loads artwork from the internet itself. Hosts contacted: `store.steampowered.com` (game names and app IDs, one request at a time, at least 1.5 s apart, answers cached 90/7 days), the Steam CDN (`*.steamstatic.com`, only if Steam has no local copy), Epic's CDN (`cdn1.epicgames.com`, URLs taken from the Epic Launcher's local catalog cache, no API call, no login) and, only with your own key, `www.steamgriddb.com` (game names, one request at a time, at least 0.6 s apart). Requests are sent as `CouchHUD/<version>`, only in the background and never while a game is running; HTTP 429/503 `Retry-After` is honored and a failing host is paused. Only complete png/jpeg/webp/gif files up to 12 MB are kept. Settings > Library (expert mode) has a switch to turn online artwork off and a button to clear the cache.
