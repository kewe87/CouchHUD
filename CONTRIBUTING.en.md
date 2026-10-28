# Contributing to CouchHUD

[Deutsch](CONTRIBUTING.md) | **English**

Great to have you here! CouchHUD aims to be a free, really good living-room frontend for Windows, and that works best together.

## Ways to contribute

- **Skins**: instructions in [docs/SKINS.en.md](docs/SKINS.en.md)
- **Plugins**: instructions in [docs/PLUGINS.md](docs/PLUGINS.md) (German), example in `plugins/CouchHud.Plugin.Wand`
- **Translations and texts**: [docs/i18n.md](docs/i18n.md)
- **Report bugs**: an issue with your Windows version, what you did and the logs from `%LOCALAPPDATA%\CouchHud\logs`
- **Code**: pull requests are welcome. For bigger things, open a short issue first so we can agree on the direction.

## Development

Requirements: .NET 10 SDK, Node.js (for the UI preview and checks), and on Windows WebView2 to try it out.

```powershell
dotnet build CouchHud.slnx
dotnet test CouchHud.slnx
dotnet run --project src/CouchHud.Shell -- --dev   # window mode with DevTools
```

`--dev` only works in a Debug build (as above with `dotnet run`). A Release build ignores the switch unless an administrator sets `HKLM\SOFTWARE\CouchHud` `DeveloperMode=1` (DWORD; the key must only be writable by Administrators, SYSTEM and TrustedInstaller). Otherwise a standard account could start CouchHUD with `--dev` from a shortcut and get DevTools.

The interface (`ui/`) is vanilla JS as ES modules without a build step. Platform-neutral logic belongs in `CouchHud.Core` (with tests in `tests/`), Windows-specific code in `CouchHud.Shell`.

### Before every push: `scripts/check.sh`

```bash
./scripts/check.sh
```

The script runs on Windows (Git Bash), Linux and macOS and does, in order:

1. `dotnet build` of the whole solution (the Windows projects too; outside Windows through `EnableWindowsTargeting`),
2. `dotnet test` of all tests,
3. **UI syntax**: every `.js`/`.mjs` file in `ui/` and `scripts/` is checked as an **ES module** (`node --input-type=module --check`; a plain `node --check` lets module errors through),
4. **i18n check** (`scripts/i18n-check.mjs`): every key in German and English with the same placeholders, every key used exists, no orphaned keys, no German text in code.

It is at least as strict as CI (which builds and tests on Windows). Red stays local, not in everyone else's inbox.

### UI without Windows: preview and screenshots

```bash
node scripts/preview-server.js          # http://localhost:5173, the complete UI with mock data (?lang=en for English)
```

`scripts/ui-shot.mjs` drives the preview in a windowless Edge (over the DevTools protocol), presses keys, switches skins, takes screenshots and checks contrasts. Good for UI changes and as evidence in a pull request:

```bash
node scripts/ui-shot.mjs --out shots skin:default wait:300 shot:home key:ArrowRight shot:next
node scripts/ui-shot.mjs --out shots --size 3840x2160 --query lang=en shot:home4k
```

Steps: `skin:<id>`, `key:<key>`, `keys:ArrowDown*3`, `wait:<ms>`, `shot:<name>`, `eval:<js>`, `contrast:<label>`, `text:<text>`. Keys: arrows, `Enter` = A, `Escape` = B, `q`/`e` = LB/RB, `x`, `y`, `m` = Start, `Home` = Guide. The path to Edge can be set with the environment variable `EDGE`.

**Screenshots for the README and docs** always with `--query demo`: the preview then shows made-up games with self-generated covers (`ui/mock-demo.js`) instead of real store covers, and no notification pop-ups. The images under `docs/` were made this way (1920×1080, scaled down to 1280×720). `node scripts/docs-shots.mjs` re-shoots all of them in German and English (`docs/<name>.png`, `docs/<name>.en.png`; for the skins image add `--catalog <folder of catalog skins>`).

**Logo and icon**: the sources are `docs/logo.svg` (from 48 px) and `assets/branding/icon-small.svg` (16–32 px), plus `docs/banner.svg` and `docs/social-preview.svg`. `node scripts/brand-render.mjs` renders all PNGs from them with Edge and rebuilds `src/CouchHud.Shell/couchhud.ico`. `ui/assets/icon.svg` is a copy of `docs/logo.svg`.

### Test VM (Hyper-V)

Windows features (shell replacement, auto sign-in, Guide button, UAC) are best tested in a Hyper-V VM, not on your work PC. The scripts run on the host over PowerShell Direct and need membership in "Hyper-V Administrators". The VM is called `Win11-Test` by default (`-VMName` changes that). Store the VM password once:

```powershell
Get-Credential | Export-Clixml "$env:USERPROFILE\couchhud-vm.cred"
```

| Script | Purpose |
|---|---|
| `scripts\deploy-vm.ps1` | Publishes CouchHUD (self-contained), copies it to `C:\Program Files\CouchHud` in the VM and starts it in the signed-in session. `-NoBuild` copies the last publish again, `-NoStart` does not start it, `-Arguments --dev` passes switches through (a Release build only honors `--dev` with `DeveloperMode=1` in `HKLM\SOFTWARE\CouchHud`, see `vm-cdp.ps1 -Enable`). |
| `scripts\vm-cdp.ps1` | Talks to the UI in the VM over the Chrome DevTools Protocol: `-Eval 'document.title'`, `-Screenshot shot.png`, `-Keys ArrowRight,Enter`, `-Console` (errors since the last call), `-Reload`. Run `-Enable` once first (sets `HKLM\SOFTWARE\CouchHud` `DeveloperMode=1` and `RemoteDebuggingPort=9222`, DWORD, as an administrator of the VM; then restart CouchHUD), and `-Disable` after the tests. The port only listens on 127.0.0.1; the couch browser gets port + 1. `WEBVIEW2_*` environment variables have no effect since 0.4.2 (CouchHUD removes them at start). |
| `scripts\vm-screenshot.ps1` | Saves the VM's console screen as a PNG (`-Path shot.png`), also at the sign-in screen, where the UI cannot be reached. |
| `scripts\vm-uac.ps1` | Confirms (or with `-Decline` declines) a UAC prompt in the VM through the virtual keyboard, which also reaches the secure desktop. |

Typical flow: `deploy-vm.ps1`, then walk through the interface with `vm-cdp.ps1 -Keys …`, check with `-Screenshot` and `-Console`, use `vm-uac.ps1` for UAC prompts and `vm-screenshot.ps1` for a black screen.

## Texts and languages (i18n)

The interface exists in German and English. Rules (details and the terminology table in [docs/i18n.md](docs/i18n.md)):

- **Every new text always comes in German and English.** UI: `ui/i18n/de/*.js` and `ui/i18n/en/*.js` (same file names, same keys), accessed with `t('area.key', { name })`. Host (C#): `Strings.Get("key", args)` in `src/CouchHud.Core/Localization/`. Installer: `[CustomMessages]` in `installer/CouchHud.iss`.
- Do not send finished German sentences from the host to the UI that the UI compares or stores: states as a **code** (`errorCode`), notifications as a key plus arguments (`Strings.Ref`).
- Only call `t()` when building the text, never in a top-level constant. Date, time and numbers with `fmtDate`, `fmtTime`, `fmtNumber`.
- Terms as in the table (e.g. "Guide button", "Skin", "Sleep timer"); paths in the settings with `›`.
- `scripts/i18n-check.mjs` (part of `check.sh`) must be green.
- Code and comments in English.

## Guiding principles

The principles: everything through the GUI, controller first, seamless full screen, third-party software in plugins, a broken plugin must never kill the shell.

- Every setting in only one place, no jargon in Simple mode.
- Every change to Windows can be undone and is listed on the transparency page ("What CouchHUD changes in Windows").

## Release

Maintainers only. The private signing key is not in the repository and not in CI, but in the maintainer's user profile (`%USERPROFILE%\.couchhud\catalog-signing.key`, protected with DPAPI).

1. Raise the version in `Directory.Build.props`, merge, set and push the tag: `git tag v0.4.0 && git push origin v0.4.0`. CI builds the setup with this version (and stops if tag and version do not match), creates the build attestation and attaches `CouchHud-Setup-<v>.exe` and `CouchHud-Setup-<v>.exe.sha256` to a GitHub release.
2. `.\scripts\sign-release.ps1 -Tag v0.4.0` (PowerShell 7, `gh` signed in): checks commit and version, downloads the setup and compares its hash with GitHub and the `.sha256`, checks the attestation and only then signs the `.sha256` (context `couchhud-release-v1`). Uploads `.sha256.sig`. **Only then do installed CouchHUDs offer the update.**
3. Edit the release notes in the GitHub release as needed (they appear shortened on the TV).

Signing scripts (PowerShell 7):

| Script | Purpose |
|---|---|
| `scripts\sign-file.ps1` | Signs a file with ECDSA P-256 (SHA-256) and writes `<file>.sig`. `-GenerateKey` creates the key pair the first time and shows the public key (which is in the code as `CatalogSignature.PublicKeyPem`). `-Context catalog` (default) or `release` keeps the purposes apart. `-BumpSerial` (catalog index only, used by `catalog-sign.ps1`) raises the root `serial` of the index by 1 before signing; the file and the `.sig` are replaced only once the signature exists. Signing never creates a key and refuses a key that is not the one compiled into CouchHUD. |
| `scripts\catalog-sign.ps1` | Signs the plugin catalog: `-Index .\catalog\index.json` raises `serial` by 1 (every change needs a higher number, or clients refuse the index as older) and writes `index.json.sig`. It never creates a key and refuses a key that is not the one compiled into CouchHUD. Do not change the file after signing (not even line endings). |
| `scripts\sign-release.ps1` | The release step above. |
| `scripts\catalog-entry.ps1` | Packs a plugin or skin folder as `<Name>-<Version>.zip` and prints the SHA-256 for the catalog entry. |

Changing catalog entries: `catalog-entry.ps1`, adjust the entry in the catalog repo's `index.json`, sign with `catalog-sign.ps1`, commit both. Format and security: [docs/PLUGINS.md](docs/PLUGINS.md#katalog) (German).

## Pull requests

This repo only publishes release states as single commits. Good suggestions are therefore taken over locally and appear with the next release; the PR is then closed with thanks instead of merged. Please do not report security vulnerabilities as an issue or PR, but as described in [SECURITY.md](SECURITY.md#english).

## License

By contributing you agree that your contribution is under the GPL-3.0 like the rest of the project.
