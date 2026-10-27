# Mitmachen bei CouchHUD

Schön, dass du da bist! CouchHUD soll ein freies, richtig gutes Wohnzimmer-Frontend für Windows werden, und das geht am besten gemeinsam.

## Wege, beizutragen

- **Skins**: Anleitung in [docs/SKINS.md](docs/SKINS.md)
- **Plugins**: Anleitung in [docs/PLUGINS.md](docs/PLUGINS.md), Beispiel in `plugins/CouchHud.Plugin.Wand`
- **Übersetzungen und Texte**: [docs/i18n.md](docs/i18n.md)
- **Fehler melden**: Issue mit Windows-Version, was du gemacht hast und den Logs aus `%LOCALAPPDATA%\CouchHud\logs`
- **Code**: Pull Requests sind willkommen. Bei größeren Dingen vorher kurz ein Issue aufmachen, damit wir die Richtung abstimmen.

## Entwickeln

Voraussetzungen: .NET 10 SDK, Node.js (für UI-Vorschau und Prüfungen), unter Windows zum Ausprobieren WebView2.

```powershell
dotnet build CouchHud.slnx
dotnet test CouchHud.slnx
dotnet run --project src/CouchHud.Shell -- --dev   # Fenstermodus mit DevTools
```

`--dev` wirkt nur in einem Debug-Build (wie oben mit `dotnet run`). Ein Release-Build ignoriert den Schalter, außer ein Administrator setzt `HKLM\SOFTWARE\CouchHud` `DeveloperMode=1` (DWORD; der Schlüssel darf nur für Administratoren, SYSTEM und TrustedInstaller beschreibbar sein). Sonst könnte ein Standardkonto CouchHUD mit `--dev` aus einer Verknüpfung starten und DevTools bekommen.

Die Oberfläche (`ui/`) ist Vanilla JS als ES-Module ohne Build-Schritt. Plattformneutrale Logik gehört nach `CouchHud.Core` (mit Tests in `tests/`), Windows-spezifisches nach `CouchHud.Shell`.

### Vor jedem Push: `scripts/check.sh`

```bash
./scripts/check.sh
```

Das Skript läuft unter Windows (Git Bash), Linux und macOS und macht der Reihe nach:

1. `dotnet build` der ganzen Solution (auch die Windows-Projekte, außerhalb von Windows über `EnableWindowsTargeting`),
2. `dotnet test` aller Tests,
3. **UI-Syntax**: jede `.js`/`.mjs`-Datei in `ui/` und `scripts/` wird als **ES-Modul** geprüft (`node --input-type=module --check`; ein einfaches `node --check` lässt Modulfehler durch),
4. **i18n-Prüfung** (`scripts/i18n-check.mjs`): jeder Schlüssel in Deutsch und Englisch mit gleichen Platzhaltern, jeder benutzte Schlüssel existiert, keine verwaisten Schlüssel, kein deutscher Text im Code.

Es ist mindestens so streng wie die CI (die baut und testet auf Windows). Rot bleibt lokal, nicht im Postfach der anderen.

### UI ohne Windows: Vorschau und Screenshots

```bash
node scripts/preview-server.js          # http://localhost:5173, komplette UI mit Mock-Daten (?lang=en für Englisch)
```

`scripts/ui-shot.mjs` steuert die Vorschau in einem Edge ohne Fenster (über das DevTools-Protokoll), drückt Tasten, wechselt Skins, macht Screenshots und prüft Kontraste. Gut für UI-Änderungen und als Beleg im Pull Request:

```bash
node scripts/ui-shot.mjs --out shots skin:ps4 wait:300 shot:home key:ArrowRight shot:next
node scripts/ui-shot.mjs --out shots --size 3840x2160 --query lang=en shot:home4k
```

Schritte: `skin:<id>`, `key:<Taste>`, `keys:ArrowDown*3`, `wait:<ms>`, `shot:<name>`, `eval:<js>`, `contrast:<label>`, `text:<text>`. Tasten: Pfeile, `Enter` = A, `Escape` = B, `q`/`e` = LB/RB, `x`, `y`, `m` = Start, `Home` = Guide. Der Pfad zu Edge lässt sich über die Umgebungsvariable `EDGE` setzen.

**Screenshots für README und Doku** immer mit `--query demo`: Die Vorschau zeigt dann ausgedachte Spiele mit selbst erzeugten Covern (`ui/mock-demo.js`) statt echter Store-Cover, und keine eingeblendete Nachricht. Die Bilder unter `docs/` sind so entstanden (1920×1080, verkleinert auf 1280×720).

**Logo und Icon**: Quellen sind `docs/logo.svg` (ab 48 px) und `assets/branding/icon-small.svg` (16–32 px), dazu `docs/banner.svg` und `docs/social-preview.svg`. `node scripts/brand-render.mjs` rendert daraus mit Edge alle PNGs und baut `src/CouchHud.Shell/couchhud.ico` neu. `ui/assets/icon.svg` ist eine Kopie von `docs/logo.svg`.

### Test-VM (Hyper-V)

Windows-Funktionen (Shell-Ersatz, Autologin, Guide-Taste, UAC) testest du am besten in einer Hyper-V-VM, nicht auf deinem Arbeits-PC. Die Skripte laufen auf dem Host über PowerShell Direct und brauchen die Mitgliedschaft in „Hyper-V-Administratoren“. Die VM heißt standardmäßig `Win11-Test` (`-VMName` ändert das). Einmalig das VM-Kennwort ablegen:

```powershell
Get-Credential | Export-Clixml "$env:USERPROFILE\couchhud-vm.cred"
```

| Skript | Zweck |
|---|---|
| `scripts\deploy-vm.ps1` | Veröffentlicht CouchHUD (self-contained), kopiert es nach `C:\Program Files\CouchHud` in der VM und startet es in der angemeldeten Sitzung. `-NoBuild` kopiert den letzten Publish erneut, `-NoStart` startet nicht, `-Arguments --dev` reicht Schalter durch (eine Release-Version beachtet `--dev` nur mit `DeveloperMode=1` in `HKLM\SOFTWARE\CouchHud`, siehe `vm-cdp.ps1 -Enable`). |
| `scripts\vm-cdp.ps1` | Spricht über das Chrome DevTools Protocol mit der UI in der VM: `-Eval 'document.title'`, `-Screenshot shot.png`, `-Keys ArrowRight,Enter`, `-Console` (Fehler seit dem letzten Aufruf), `-Reload`. Vorher einmal `-Enable` (setzt als Administrator der VM `HKLM\SOFTWARE\CouchHud` `DeveloperMode=1` und `RemoteDebuggingPort=9222`, DWORD; danach CouchHUD neu starten), nach den Tests `-Disable`. Der Port hört nur auf 127.0.0.1, der Couch-Browser bekommt Port + 1. `WEBVIEW2_*`-Umgebungsvariablen wirken seit 0.4.2 nicht mehr (CouchHUD entfernt sie beim Start). |
| `scripts\vm-screenshot.ps1` | Speichert den Konsolen-Bildschirm der VM als PNG (`-Path shot.png`), auch am Anmeldebildschirm, wo die UI nicht erreichbar ist. |
| `scripts\vm-uac.ps1` | Bestätigt (oder mit `-Decline` verneint) eine UAC-Abfrage in der VM über die virtuelle Tastatur, die auch den sicheren Desktop erreicht. |

Typischer Ablauf: `deploy-vm.ps1`, dann mit `vm-cdp.ps1 -Keys …` durch die Oberfläche gehen, mit `-Screenshot` und `-Console` prüfen, bei UAC-Dialogen `vm-uac.ps1`, bei schwarzem Bild `vm-screenshot.ps1`.

## Texte und Sprachen (i18n)

Die Oberfläche gibt es auf Deutsch und Englisch. Regeln (Details und Begriffstabelle in [docs/i18n.md](docs/i18n.md)):

- **Jeder neue Text kommt immer in Deutsch und Englisch.** UI: `ui/i18n/de/*.js` und `ui/i18n/en/*.js` (gleiche Dateinamen, gleiche Schlüssel), Zugriff mit `t('bereich.schluessel', { name })`. Host (C#): `Strings.Get("key", args)` in `src/CouchHud.Core/Localization/`. Installer: `[CustomMessages]` in `installer/CouchHud.iss`.
- Keine fertigen deutschen Sätze aus dem Host an die UI schicken, die die UI vergleicht oder speichert: Zustände als **Code** (`errorCode`), Benachrichtigungen als Schlüssel plus Argumente (`Strings.Ref`).
- `t()` erst beim Bauen des Textes aufrufen, nie in einer Konstante auf oberster Ebene. Datum, Zeit und Zahlen mit `fmtDate`, `fmtTime`, `fmtNumber`.
- Begriffe wie in der Tabelle (z.B. „Guide-Taste“, „Skin“, „Ausschalt-Timer“); Pfade in den Einstellungen mit `›`.
- `scripts/i18n-check.mjs` (Teil von `check.sh`) muss grün sein.
- Code und Kommentare auf Englisch.

## Leitplanken

Die Grundsätze stehen in [docs/ROADMAP.md](docs/ROADMAP.md#leitplanken): alles über die GUI, Controller zuerst, nahtloser Fullscreen, Fremdsoftware in Plugins, ein kaputtes Plugin darf nie die Shell killen.

- Jede Einstellung nur an einer Stelle, im Einfach-Modus keine Fachbegriffe.
- Jeder Eingriff in Windows ist umkehrbar und steht in der Transparenz-Seite („Was CouchHUD an Windows ändert“).

## Release

Nur für Maintainer. Der private Signaturschlüssel liegt nicht im Repository und nicht in der CI, sondern im Benutzerprofil des Maintainers (`%USERPROFILE%\.couchhud\catalog-signing.key`, DPAPI-geschützt).

1. Version in `Directory.Build.props` anheben, mergen, Tag setzen und pushen: `git tag v0.4.0 && git push origin v0.4.0`. Die CI baut das Setup mit dieser Version (bricht ab, wenn Tag und Version nicht zusammenpassen), erzeugt die Build-Attestierung und hängt `CouchHud-Setup-<v>.exe` und `CouchHud-Setup-<v>.exe.sha256` an ein GitHub-Release.
2. `.\scripts\sign-release.ps1 -Tag v0.4.0` (PowerShell 7, `gh` angemeldet): prüft Commit und Version, lädt das Setup und vergleicht den Hash mit GitHub und der `.sha256`, prüft die Attestierung und signiert erst dann die `.sha256` (Kontext `couchhud-release-v1`). Lädt `.sha256.sig` hoch. **Erst damit bieten installierte CouchHUDs das Update an.**
3. Release-Notes nach Bedarf im GitHub-Release nachbearbeiten (sie erscheinen gekürzt auf dem TV).

Signier-Skripte (PowerShell 7):

| Skript | Zweck |
|---|---|
| `scripts\sign-file.ps1` | Signiert eine Datei mit ECDSA P-256 (SHA-256) und schreibt `<Datei>.sig`. `-GenerateKey` legt das Schlüsselpaar beim ersten Mal an und zeigt den öffentlichen Schlüssel (der im Code als `CatalogSignature.PublicKeyPem` steht). `-Context catalog` (Standard) oder `release` trennt die Verwendungszwecke. |
| `scripts\catalog-sign.ps1` | Signiert den Plugin-Katalog: `-Index .\catalog\index.json` schreibt `index.json.sig`. Die Datei vor dem Signieren nicht mehr ändern (auch keine Zeilenenden). |
| `scripts\sign-release.ps1` | Der Release-Schritt oben. |
| `scripts\catalog-entry.ps1` | Packt ein Plugin- oder Skin-Verzeichnis als `<Name>-<Version>.zip` und gibt die SHA-256 für den Katalogeintrag aus. |

Katalogeinträge ändern: `catalog-entry.ps1`, Eintrag in der `index.json` des Katalog-Repos anpassen, mit `catalog-sign.ps1` signieren, beides committen. Format und Sicherheit: [docs/PLUGINS.md](docs/PLUGINS.md#katalog).

## Pull Requests

Dieses Repo veröffentlicht nur Release-Stände als einzelne Commits. Gute Vorschläge werden deshalb lokal übernommen und erscheinen mit dem nächsten Release; der PR wird dann mit Dank geschlossen statt gemergt. Sicherheitslücken bitte nicht als Issue oder PR melden, sondern wie in [SECURITY.md](SECURITY.md) beschrieben.

## Lizenz

Mit einem Beitrag stimmst du zu, dass er unter der GPL-3.0 steht wie der Rest des Projekts.
