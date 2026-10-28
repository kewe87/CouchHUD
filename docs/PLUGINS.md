# Plugins für CouchHUD

Plugins erweitern CouchHUD um neue Spiele-Quellen, Seiten, Einstellungen, Einträge im Guide-Menü und Werkzeuge für den KI-Assistenten. Sie sind normale .NET-Klassenbibliotheken gegen `CouchHud.Sdk`.

Das Beispiel im Repository ist **[plugins/CouchHud.Plugin.Wand](../plugins/CouchHud.Plugin.Wand/WandPlugin.cs)**: eine Datei, die Einstellungen, eine Seite, eine Quick-Action, ein Tool und Events nutzt.

## In fünf Minuten zum eigenen Plugin

```powershell
dotnet new classlib -n CouchHud.Plugin.Hallo -f net10.0
cd CouchHud.Plugin.Hallo
# Referenz auf das SDK (aus dem CouchHUD-Repo oder der CouchHud.Sdk.dll neben der CouchHud.exe)
dotnet add reference ..\couchhud\src\CouchHud.Sdk\CouchHud.Sdk.csproj
```

In der `.csproj` das SDK nicht mitkopieren, CouchHUD bringt es selbst mit:

```xml
<ProjectReference Include="..\couchhud\src\CouchHud.Sdk\CouchHud.Sdk.csproj" Private="false" ExcludeAssets="runtime" />
<PropertyGroup><EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup>
```

```csharp
using CouchHud.Sdk;

public sealed class HalloPlugin : IPlugin
{
    public string Id => "hallo";

    public void Initialize(IPluginContext context)
    {
        context.RegisterQuickAction(new QuickAction
        {
            Id = "wave",
            Icon = "👋",
            Label = "Hallo sagen",
            Run = _ => Task.FromResult<string?>("Hallo vom Plugin!")
        });

        context.Events.GameStarted += game => context.Notify("Viel Spaß", game.Name);
    }
}
```

Bauen und nach `%APPDATA%\CouchHud\plugins\CouchHud.Plugin.Hallo\` kopieren (Ordnername = Name der DLL). CouchHUD neu starten, fertig. Alternativ den Ordner als `.zip` packen und in CouchHUD unter *Einstellungen › Expertenmodus › Plugins & Skins › Plugin installieren* auswählen.

## Was ein Plugin kann (SDK v2)

| Methode | Wofür |
|---|---|
| `RegisterLibraryProvider(ILibraryProvider)` | Spiele oder Apps für die Bibliothek (Kodi, GOG, Emulatoren …) |
| `RegisterSettings(params PluginSetting[])` | Einstellungen (Toggle, Text, Geheim, Auswahl). CouchHUD zeigt und speichert sie, lesen über `context.Settings` |
| `RegisterPage(PluginPage)` | Eine Listen-Seite mit Zeilen (`PluginRow`), A auf einer Zeile ruft `Activate` auf |
| `RegisterQuickAction(QuickAction)` | Eintrag im Guide-Menü, z.B. „Licht aus“ |
| `RegisterTool(PluginTool)` | Werkzeug für den KI-Assistenten und MCP-Clients. Der Name wird zu `plugin_<plugin>_<werkzeug>`; ein Name, den es schon gibt, wird abgelehnt. Mit `Writes = true` bestätigt der Nutzer vorher am TV (Assistent); über MCP wird **jedes** Plugin-Werkzeug bestätigt, die Frage zeigt die Argumente. Bei gesperrter Couch laufen Plugin-Werkzeuge nicht |
| `Notify(title, body)` | Hinweis auf dem TV und in der Benachrichtigungsliste |
| `OpenUrl(url)` | Öffnet eine http(s)-Adresse im eingebauten CouchHUD-Browser (nie im Windows-Standardbrowser), nur bei entsperrter Couch; `false`, wenn abgelehnt |
| `Events` | `GameStarted`, `GameEnded` (mit Spielzeit), `PcModeEntered`, `CouchModeEntered`, `LanguageChanged`, `LockChanged` (SDK 2) |
| `Locked` | **SDK 2:** `true`, solange die Couch gesperrt ist (ein Gast oder Kind am Controller). Dann nichts auf eigene Faust starten oder umschalten, z.B. keinen Autostart bei `GameStarted`. Ältere CouchHUD-Versionen antworten `false` |
| `Language` | Die Sprache von CouchHUD jetzt: `"de"` oder `"en"` |
| `Settings` | Gespeicherte Werte, `Get`, `GetBool`, `Set` |
| `DataDirectory` | Eigener, beschreibbarer Ordner für Cache und Dateien |

### Zusätze im SDK 3 für Zugangsdaten, Menüeinträge und Web-Anmeldungen

| Mitglied | Fähigkeit | Wofür |
|---|---|---|
| `Settings.SetSecret(key, value)` | keine | Das Gleiche wie `Set`, aber der Host speichert den Wert verschlüsselt (Windows-Konto, an die Plugin-Kennung gebunden); `Get` liefert ihn nur diesem Plugin. Einstellungen der Art `Secret` werden ebenso verschlüsselt. Ohne Verschlüsselung im Host wird nichts gespeichert |
| `RemoveQuickAction(id)` | `quickActions` | Nimmt einen Eintrag im Guide-Menü wieder heraus |
| `QuickAction.AllowedWhileLocked`, `PluginTool.AllowedWhileLocked` | `quickActions`, `assistant` | Der Eintrag im Guide-Menü beziehungsweise das Werkzeug darf auf der gesperrten Couch laufen. Zählt nur für CouchHUDs eigene Plugins; bei jedem anderen wird der Eintrag gesperrt nicht gezeigt und ein Aufruf abgelehnt |
| `SignIn.Open(seite, host, empfänger)` | `browser.signin` (nur CouchHUDs eigene Plugins) | Öffnet eine Anmeldeseite im Couch-Browser. Der Browser bricht jede Navigation zu `host` (muss auf `.invalid` enden) vor jeder Anfrage ab und gibt die Adresse einmal an `empfänger`; nur entsperrt, danach schließt der Browser |

**Wichtig zu Geheimnissen:** Plugins laufen im Prozess von CouchHUD und als derselbe Windows-Benutzer. Die Verschlüsselung durch `SetSecret` (DPAPI mit der Plugin-Kennung als Zusatz) trennt die Dateien der Plugins, ist aber **keine Schranke gegen ein bösartiges Plugin**, das selbst DPAPI aufruft oder fremde Dateien liest. Darum lehnt der Lint `ProtectedData` für Fremdentwickler ab. Dateizugriffe außerhalb des eigenen Ordners erkennt der Lint nicht zuverlässig (offener Punkt).

Ein Beispiel für alles zusammen ist das Home-Assistant-Plugin (eigenes Repository `CouchHud.Plugin.HomeAssistant`): Adresse und Token als Einstellungen, die Anmeldung als Seite, die Geräte als Kacheln.

### Dienste des Hosts für CouchHUDs eigene Plugins

Drei Fähigkeiten gibt es nur für CouchHUDs eigene Plugins (ein Fremdentwickler kann sie nicht einmal angeben); jeweils nennt das Plugin ein Programm oder eine Datei, der Host prüft sie noch einmal selbst:

| Mitglied | Fähigkeit | Wofür |
|---|---|---|
| `RunSetupAsync(pfad, sha256)` | `setup.run` | Übergibt ein heruntergeladenes Setup an Windows (die Administrator-Abfrage fragt immer). Der Host verlangt die entsperrte Couch, kein Kinderprofil und einen Druck auf eine Zeile oder einen Guide-Eintrag dieses Plugins mit `RequiresParentGrant` (Einmal-Zeichen, höchstens 35 Minuten), erlaubt ein Setup zur Zeit, nimmt nur eine .exe im eigenen Datenordner dieses Plugins (allein in ihrem Ordner), rechnet den SHA-256 an der gehaltenen Datei noch einmal und prüft ihren echten Pfad. Gestartet wird über die Windows-Administrator-Abfrage. Liefert false bei Ablehnung |
| `RestrictWhileLocked(exe)` | `lock.restrict` | Fenster dieses Programms (echter Pfad, unterhalb von Program Files) werden auf der gesperrten Couch minimiert. Nur eine Beschränkung, nie eine Erlaubnis |
| `AdaptStartupArgs(exe, funktion)` | `startup.args` | Wenn CouchHUD als Shell die Autostart-Einträge ausführt, laufen die Argumente des Eintrags dieses Programms durch `funktion` (null lässt sie, wie sie sind; eine Sekunde, ohne Steuerzeichen, höchstens 1024 Zeichen). Das Programm bleibt das im Eintrag |

Dazu kommen die Fähigkeiten `process`, `registry`, `native`, `network:loopback` und `network:internet`, die der Code-Lint beim Plugin findet und die es angeben muss.

Das VPN-Plugin (eigenes Repository `CouchHud.Plugin.Vpn`, Kennung `vpn`) ist das kleinste Beispiel für ein Programm, das ein Plugin nur bedient: eine Seite mit Status, Schalter und Auswahl, ein Eintrag im Guide-Menü, nur die Rechte `pages`, `quickActions`, `process`, `registry` und `native`. Gesperrte Couch: nur das Ansehen ist mit `AllowedWhileLocked` markiert, jede Änderung hat `RequiresParentGrant` (Eltern-Code unter Jugendschutz) und ist nicht markiert; das Plugin prüft `Locked` vor jeder Änderung noch einmal selbst. Das Plugin hat keinen Reiter. Das VPN im Programm selbst gibt es nicht mehr; die zwei Einstellungen von 0.5 (*VPN im Guide-Menü*, zuletzt gewähltes Land) werden einmal übernommen (`PluginSettingSeeds`).

Das CaseHUD-Plugin (eigenes Repository `CouchHud.Plugin.CaseHud`, `docs/CASEHUD.md`) nutzt alle drei: Seite mit Kacheln und Vorschaubildern, Stufenreglern und Auswahlen, Einträge im Guide-Menü für den gesperrten Couch-Modus (`AllowedWhileLocked`), die Beschränkung seines Fensters auf der gesperrten Couch und die erste Installation. Seine Einstellung aus 0.5 übernimmt es einmal (`PluginSettingSeeds`, nur für CouchHUDs eigenes Plugin der Kennung `casehud`); eine Anbindung im Programm selbst gibt es nicht mehr.

**Deklarativ statt HTML**: Plugins beschreiben Seiten als Daten. CouchHUD rendert sie im aktiven Skin, mit Controller, Maus und Tastatur. Dein Plugin sieht in jedem Skin richtig aus, ohne eine Zeile CSS.

## Eigene Reiter (Fähigkeit `tabs`)

Ein Plugin kann Seiten, die es mit `RegisterPage` beschreibt, zusätzlich als **Reiter in der Kopfleiste** zeigen. Dafür deklarierst du die Fähigkeit `"tabs"` in `plugin.json` (sie steht in der Berechtigungsliste und im Katalog) und meldest den Reiter an:

```csharp
context.RegisterPage(new PluginPage { Id = "home", Title = "Smart Home", Rows = LoadRows, Sections = sections });
context.RegisterTab(new PluginTab("home", "home", "Smart Home", "Smart Home")
{
    Glyph = "🏠",      // genau ein Symbol; oder Icon = pngBytes (PNG bis 64 KB, höchstens 256 x 256 Pixel)
    Area = "always",   // games | apps (Standard) | browser | news | always (nur eigene Plugins von CouchHUD)
    ChildSafe = true   // auch im Kinderprofil zeigen (nur eigene Plugins von CouchHUD)
});
```

`PluginTab(Id, PageId, TitleDe, TitleEn)`: `PageId` ist eine Seite **desselben** Plugins; die Seite zeichnet CouchHUD wie jede Plugin-Seite (Kacheln, Zeilen, Regler, Auswahl, Verweise), ein Plugin bringt nie HTML oder Skript mit. Meldet das Plugin `Invalidate(pageId)`, lädt sich der offene Reiter neu. Eine Anmeldung mit derselben Id ersetzt die frühere. Beide Titel gelten für beide Sprachen, du musst bei einem Sprachwechsel nichts neu melden.

| Regel | Wert |
|---|---|
| Reiter je Plugin / insgesamt | 2 / 6 (CouchHUDs eigene Plugins zuerst, dann die anderen in der Reihenfolge der Anmeldung; was darüber hinausgeht, wird ignoriert) |
| Kennung | `a-z`, `0-9`, `-`, `_`, bis 32 Zeichen |
| Titel | 1 bis 14 Zeichen (die Leiste ist schmal), lateinische Buchstaben einschließlich Umlauten und Akzenten (`Küche`, `Übersicht`), Ziffern, Leerzeichen und `- & . ' + /`; Verwechslungsbuchstaben wie `ı` oder `ł` und andere Schriften werden abgelehnt; keine Steuer-, Bidi- oder Nullbreitenzeichen; wenigstens eine Sprache |
| Gesperrte Titel | Home, Favoriten/Favorites, Spiele/Games, Apps, Browser, Downloads, News/Nachrichten, Assistent/Assistant, Einstellungen/Settings, Katalog, Plugins, Guide, Profile, Eltern, Sicherheit, CouchHUD und die Namen von CouchHUDs eigenen Plugins, auch mit Ziffern statt Buchstaben, getrennt geschrieben oder als Teil eines längeren Titels (`Apps Hub`). „Smart Home“ darf nur ein eigenes Plugin von CouchHUD tragen |
| Symbol | `Glyph` (genau ein Emoji, Pfeil oder eine Form, keine Buchstaben oder Ziffern) oder `Icon` (PNG bis 64 KB und 256 Pixel je Seite); ein ungültiges Symbol kostet nur das Symbol. Fremde Plugins können kein Symbol nehmen, das CouchHUD selbst als eigenes Zeichen zeichnet (Schloss, Zahnrad, Glocke …): sie bekommen einen schlichten Punkt. Zeigt die Leiste mehr als drei Plugin-Reiter, entfallen die Symbole |
| `Area` | `games`, `apps` (Standard), `browser`, `news`. `always` (nie von einem Bereich ausgeblendet) gilt nur für CouchHUDs eigene Plugins; bei jedem anderen zählt es als `apps` |
| `ChildSafe` | Der Reiter erscheint auch im Kinderprofil. Wirkt nur bei CouchHUDs eigenen Plugins (z. B. ein Smart-Home-Reiter, der vorher schon für Kinder sichtbar war); bei allen anderen wird es ignoriert. Die Seite dahinter bleibt hinter den üblichen Schranken |

Wo der Reiter steht und wann er fehlt: nach den eingebauten Inhalts-Reitern, vor Assistent und Einstellungen. Er fehlt, wenn das Plugin aus oder nicht geladen ist, wenn sein **Bereich** (`Area`) ausgeschaltet ist, wenn die Seite nicht (mehr) angemeldet ist und **in Kinderprofilen, außer bei einem eigenen Plugin von CouchHUD mit `ChildSafe`**. Auf der gesperrten Couch bleiben nur die Reiter von CouchHUDs eigenen Plugins; die Seite zeigt nur, was `AllowedWhileLocked` freigibt, und bedienen lassen sich dort nur Zeilen von CouchHUDs eigenen Plugins (wie bei jeder Plugin-Seite). Schaltet der Nutzer das Plugin aus oder deinstalliert es, verschwindet der Reiter sofort. Alles, was eine Zeile ändert, geht durch dieselben Schranken wie sonst (Entsperren, Eltern-Code).

## Wer darf was: Funktionen

Ein Plugin kann benannte **Funktionen** anmelden und seine Seiten (`PluginPage.Function`), Zeilen (`PluginRow.Function`, sonst gilt die der Seite), Menüeinträge (`QuickAction.Function`), Reiter (`PluginTab.Function`) und Werkzeuge (`PluginTool.Function`) damit markieren:

```csharp
context.RegisterFunctions(
    new PluginFunction("lighting", "Beleuchtung", "Lighting") { Suggested = PluginLevel.Restricted },
    new PluginFunction("settings", "Einstellungen", "Settings"));   // Admin ist der Standard
```

Der Besitzer wählt pro Funktion in *Plugins › (Plugin)*: **Nur Admin** (`admin`), **Auch eingeschränkt** (`restricted`: auch gesperrte Couch und Kinderprofil) oder **Niemand** (`off`: ausgeblendet, Aufrufe abgelehnt). `Suggested` ist nur der Startwert, und nur bei CouchHUDs eigenen Plugins zählt `Restricted`; bei jedem anderen Plugin startet die Funktion als *Nur Admin*, der Besitzer kann sie freigeben. Der Host erzwingt die Stufe vor jedem Zeigen und Ausführen; ein markierter Eintrag folgt allein der Stufe, seine Flags (`AllowedWhileLocked`, `RequiresParentGrant`) gelten nur für nicht markierte. Bei *Auch eingeschränkt* fragt der Host nie nach dem Eltern-Code; wer das nicht will, lässt die Funktion bei *Nur Admin*. Eine Funktion, die nicht angemeldet wurde, zählt als *Nur Admin*. Prüfe nicht selbst strenger oder lockerer: Entscheidet der Host, dass eine Funktion erlaubt ist, muss die Seite auch ohne weitere eigene Sperre laufen. Im Kinderprofil verlangt eine Admin-Funktion den Eltern-Code (eine Seite öffnet sich damit für einen Besuch von höchstens 5 Minuten, eine Zeile oder ein Eintrag ohne Admin-Seite fragt bei jedem Druck); für Werkzeuge ist sie dort verboten. Plugins, die Funktionen anmelden, brauchen CouchHUD 0.6.0 oder neuer (`minCouchHud` 0.6.0). `RegisterFunctions` braucht keine Fähigkeit und keinen neuen SDK-Stand; ein älterer Host ignoriert es, die alten Flags gelten dann weiter. Höchstens zwölf Funktionen, Kennung `a-z0-9-_` bis 32 Zeichen.

## plugin.json

Optional, aber empfohlen, liegt neben der DLL:

```json
{
  "name": "Hallo",
  "version": "1.0.0",
  "author": "dein-name",
  "description": "Sagt Hallo",
  "homepage": "https://github.com/dein-name/couchhud-hallo",
  "sdk": 1
}
```

`description` ist ein Text oder pro Sprache `{ "de": "Sagt Hallo", "en": "Says hello" }`. CouchHUD zeigt die Beschreibung in der eingestellten Sprache und fällt auf die andere zurück.

`sdk` ist die SDK-Version, die das Plugin braucht. Ältere CouchHUD-Versionen laden es dann gar nicht erst, statt abzustürzen. SDK 2 ist abwärtskompatibel: Plugins für SDK 1 laufen unverändert weiter, `Locked` und `LockChanged` sind nur Zusätze (mit Standardimplementierung, ein Plugin, das sie nutzt, läuft auch auf einem älteren CouchHUD, dort ist `Locked` immer `false`). Wer sie braucht, schreibt `"sdk": 2` nur, wenn das Plugin ohne sie nicht sicher ist.

## Zwei Sprachen

CouchHUD gibt es auf Deutsch und Englisch, und die Sprache lässt sich zur Laufzeit umstellen. Dein Plugin liest sie mit `context.Language` (`"de"` oder `"en"`; ältere CouchHUD-Versionen melden immer `"de"`, das Plugin läuft also weiter).

- Texte, die du **auf Abruf** baust (Zeilen einer Seite, `Detail` einer Quick-Action, Rückgabe von `Activate`), fragst du bei jedem Aufruf neu: `context.Language == "en" ? "Open" : "Öffnen"`.
- Texte, die du **einmal registrierst** (`PluginSetting.Label`/`Description`, `PluginPage.Title`/`Description`, `QuickAction.Label`), gelten für die Sprache zum Zeitpunkt der Registrierung. Registriere sie bei `Events.LanguageChanged` noch einmal: eine Registrierung mit derselben Id (`Key`, `Id`) ersetzt die frühere.
- Die Beschreibung der `PluginTool`s bleibt Englisch, das verstehen die Modelle am besten.
- `description` in `plugin.json` und im Katalog gibt es pro Sprache (siehe oben und [Format](#format)).

```csharp
public void Initialize(IPluginContext context)
{
    _context = context;
    Register();
    context.Events.LanguageChanged += _ => Register();
}

private string T(string de, string en) => _context.Language == "en" ? en : de;

private void Register() => _context.RegisterQuickAction(new QuickAction
{
    Id = "open",
    Label = T("Öffnen", "Open"),
    Run = _ => Task.FromResult<string?>(null)
});
```

Das Wand-Plugin (`plugins/CouchHud.Plugin.Wand`) macht genau das.

## Regeln, damit CouchHUD stabil bleibt

- Jeder Aufruf in ein Plugin (Seiten, Aktionen, Tools) hat ein Timeout von 15 Sekunden. Langsame Arbeit gehört in `Task.Run` oder einen Hintergrund-Task.
- `QuickAction.Detail` (die zweite Zeile im Guide-Menü) wird auf einem Hintergrund-Thread mit 2 Sekunden Timeout abgefragt, nie auf dem UI-Thread. Antwortet es nicht, hat der Eintrag keine zweite Zeile; solange ein Aufruf hängt, wird kein neuer gestartet. Der Takt von Sperre und Jugendschutz hängt davon nicht ab.
- Exceptions werden abgefangen und als Meldung angezeigt. Ein kaputtes Plugin legt nie die Shell lahm.
- Event-Handler laufen im Hintergrund, nie auf dem UI-Thread.
- Jedes Plugin hat einen eigenen Load-Context: eigene NuGet-Abhängigkeiten in eigenen Versionen sind erlaubt.
- Plugins laufen mit den Rechten des Benutzers. Bitte nichts tun, was der Nutzer nicht erwartet, und Secrets nur über `SettingKind.Secret` speichern.
- **Freigabe:** CouchHUD merkt sich beim Installieren (Katalog, `.zip`) einen Hash der Dateien jedes Plugins. Ein Ordner in `%APPDATA%\CouchHud\plugins`, den niemand über CouchHUD installiert hat, oder dessen Dateien sich geändert haben (auch von Hand kopiert!), lädt erst nach einer Freigabe (*Plugins & Skins* › Plugin › *Plugin freigeben*, mit dem Code der Dateien, nur entsperrt). Wer sein Plugin beim Entwickeln direkt in den Ordner baut, gibt es nach jeder Änderung neu frei, oder installiert es als `.zip`. Plugins, die schon vor dieser Funktion dort lagen, gelten beim ersten Start als freigegeben. Ein vorgemerktes Katalog-Update wird vor dem Einspielen erneut gegen die Prüfsumme geprüft. In den Hash gehen alle Dateien des Ordners ein, außer `desktop.ini`, `Thumbs.db` und `*.tmp` (die legen Windows und Programme von allein an). Die Liste der freigegebenen Plugins (`plugin-trust.json`) trägt eine Signatur (HMAC, derselbe Schlüssel wie bei `playtime.json`, mit DPAPI an das Windows-Konto gebunden). **Wogegen das schützt:** gegen ein Programm oder Skript, das nur Dateien schreibt, einen fremden Ordner ablegt und die Liste von Hand ergänzt oder ersetzt, und gegen eine hineinkopierte Liste. **Wogegen nicht:** ein Programm, das als dasselbe Konto läuft, kann den Schlüssel auch lesen; die Freigabe ersetzt keinen Virenschutz. Passt die Signatur nicht oder ist die Liste unlesbar, meldet CouchHUD einen Fehler an den Plugins, lädt nichts Unbekanntes und überschreibt die Datei nicht; zum Neubeginnen `plugin-trust.json` löschen. Danach gilt, was dann im Plugin-Ordner liegt, beim nächsten Start als freigegeben (wie beim allerersten Start): vorher prüfen, dass dort nur Plugins liegen, die du kennst.

## Plugin teilen

Repository auf GitHub mit dem Topic `couchhud-plugin` anlegen und ein Release mit dem `.zip` (Ordner mit DLL, Abhängigkeiten und `plugin.json`) hochladen. So finden andere es. Wer will, dass es direkt in CouchHUD erscheint, trägt es im [Katalog](#katalog) ein.

## Katalog

CouchHUD kann Plugins und Skins aus einem **Katalog** installieren und aktualisieren: *Einstellungen › Darstellung › Neue Skins installieren* (oder im Expertenmodus *Plugins & Skins*, dort *Katalog*) (mit LB/RB zwischen *Installiert* und *Katalog* wechseln). Ein Katalog ist eine einzelne `index.json` irgendwo im Netz. Beispiel: [catalog/index.json](../catalog/index.json).

Der Standard-Katalog liegt unter `https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json`. Er ist mit CouchHUDs Schlüssel signiert, immer dabei und lässt sich weder entfernen noch ausschalten.

Weitere Kataloge von anderen Entwicklern kommen nur über die Oberfläche des HUD hinein (Expertenmodus, Eltern-Code, nie bei gesperrter Couch oder im Kinderprofil), nur über https und erst nach der Warnung, dass du dem Entwickler vertrauen musst: Er könnte Schadcode einschleusen. Sie stehen in `ExtraCatalogSources` (höchstens 8; Adresse, optional der festgehaltene Schlüssel des Katalogs, Name). Alles aus einem solchen Katalog ist Fremdentwickler: ein Plugin braucht die Signatur seines Entwicklers (Paketangabe), Namen von CouchHUDs Plugins und Kennungen des Standard-Katalogs sind gesperrt, und die Größe des Index ist begrenzt (512 KB, 200 Einträge). Bei gleicher `id` gewinnt immer der Standard-Katalog.

```json
"CatalogUpdateCheck": true
```

Die alte Liste `CatalogSources` in der `config.json` wirkt seit 0.6 nur noch zusammen mit dem Entwickler-Schalter (siehe unten).

### Oberfläche (Fernseher)

*Plugins & Skins › Katalog* ist eine Liste wie ein Homebrew-Kanal: Icon, Name, Kurzbeschreibung, Herausgeber, Version, Größe. Plugins von Fremdentwicklern tragen dauerhaft das Zeichen „Fremdentwickler“. **A** öffnet die Detailseite (Langbeschreibung, Screenshots, Berechtigungen in einfachen Worten, Lizenz, Änderungen), **Y** installiert oder aktualisiert direkt aus der Liste. Jede Änderung geht durch dieselben Schranken in derselben Reihenfolge: Warnung (bei Fremdentwicklern mit Entwickler, Schlüssel und Berechtigungen, *Abbrechen* ist vorgewählt), dann der Eltern-Code, dann erst der Aufruf. Der Host prüft alles noch einmal; die Seite bietet nur an, was er erlaubt.

Der Reiter *Quellen* gibt es nur im Expertenmodus. CouchHUDs Katalog steht oben („Offiziell“, nicht entfernbar), weitere Kataloge zeigen Name und Host. Hinzufügen: Adresse (und optional der Schlüssel) mit der Bildschirmtastatur, die volle Warnung, der Eltern-Code, dann erst der Aufruf mit der Bestätigung.

Seiten, die ein Plugin beschreibt (`RegisterPage`), zeichnet `ui/views/plugin-page.js`: Kacheln, Bilder (nur PNG), Schalter, Stufenregler (`Stepper`, mit `SettleMs`), Auswahl, Links auf Unterseiten; meldet das Plugin `Invalidate(pageId)`, wird die offene Seite neu geladen.

### Format

```json
{
  "version": 1,
  "serial": 1,
  "entries": [
    {
      "id": "wand",
      "folder": "CouchHud.Plugin.Wand",
      "name": "Wand",
      "type": "plugin",
      "version": "1.0.0",
      "minCouchHud": "0.3.0",
      "author": "kewe87",
      "description": { "de": "Cheats und Mods …", "en": "Cheats and mods …" },
      "homepage": "https://github.com/…",
      "icon": "https://…/wand.png",
      "download": "https://…/CouchHud.Plugin.Wand-1.0.0.zip",
      "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    }
  ]
}
```

| Feld | Pflicht | Bedeutung |
|---|---|---|
| `id` | ja | Eindeutige Kennung, `A-Z a-z 0-9 . _ -`, bis 64 Zeichen |
| `name` | ja | Anzeigename |
| `names` | nein | `{ "de": …, "en": … }`: der Anzeigename in der Sprache von CouchHUD (ab 0.6); ältere Versionen lesen `name` |
| `type` | ja | `plugin` oder `skin` |
| `version` | ja | SemVer (`1.2.3`, `1.3.0-beta.1`). Der Katalog zeigt „Update“, sobald sie höher ist als die installierte |
| `download` | ja | **https**-Adresse eines `.zip` (Aufbau wie bei der manuellen Installation, siehe oben und [SKINS.md](SKINS.md)) |
| `sha256` | ja | SHA-256 des `.zip`, 64 Hex-Zeichen. Ohne passende Prüfsumme wird nichts installiert |
| `description` | nein | Text oder `{ "de": …, "en": … }` |
| `author`, `homepage`, `icon` | nein | `homepage` und `icon` nur als https |
| `minCouchHud` | nein | Kleinste CouchHUD-Version. Ist die laufende kleiner, bleibt der Eintrag sichtbar, ist aber nicht installierbar |
| `folder` | nein | Ordnername des Pakets, Standard ist die `id`. Bei Plugins der Name der DLL und des Ordners (`CouchHud.Plugin.Wand`) |

Einträge ohne `id`, `name`, gültigen `type` oder gültige `version` werden übersprungen. Einträge ohne gültige Prüfsumme oder mit einer Adresse ohne https werden angezeigt, lassen sich aber nicht installieren. Ein kaputter Index (kein JSON) ist ein Fehler der Quelle; CouchHUD zeigt dann den zuletzt gespeicherten Stand.

Das `.zip` muss genau einen Ordner namens `folder` enthalten (oder die Dateien direkt, dann wird `folder` als Name genommen). Die Hilfe `scripts\catalog-entry.ps1 -Folder <Ordner> -Version 1.0.0` packt den Ordner richtig und gibt die Prüfsumme für den Eintrag aus.

### Vertrauen: Prüfsumme und Signatur

Zwei Schutzschichten mit verschiedenen Aufgaben:

- Die **Prüfsumme** (`sha256` im Eintrag) schützt gegen beschädigte oder ausgetauschte **Downloads**: Das `.zip` muss genau dem Hash im Index entsprechen.
- Die **Signatur** schützt gegen einen manipulierten **Index**. Wer nur `index.json` ändern kann (z.B. mit einem gekaperten GitHub-Konto), könnte sonst Download-URL und Hash zusammen austauschen. Der Standard-Katalog liefert deshalb neben `index.json` eine `index.json.sig`: Base64 einer ECDSA-P-256-Signatur (SHA-256, IEEE P1363) über die **exakten Bytes** der `index.json`. Den öffentlichen Schlüssel bringt CouchHUD mit (`CatalogSignature.PublicKeyPem`). Ohne gültige Signatur ist die Standardquelle „nicht vertrauenswürdig“, und nichts daraus ist installierbar. Bei einer kaputten Signatur zeigt CouchHUD den zuletzt geprüften gespeicherten Stand.
- Die **Seriennummer** (`serial`, ganze Zahl im Index, von der Signatur mit abgedeckt) schützt gegen das **Zurückspielen eines alten, echt signierten Index** (zum Beispiel über einen Proxy oder einen veralteten Cache), der die `revoked`-Liste von heute wieder entfernen würde. Sie muss bei **jeder** Änderung des Index steigen (`catalog-sign.ps1` erhöht sie vor dem Signieren selbst um 1). CouchHUD lehnt einen geladenen Index ab, dessen Nummer **kleiner** ist als die der gespeicherten Kopie derselben Adresse; es bleibt bei der gespeicherten Kopie, und die Meldung steht in der Fehlerzeile. Gleich ist erlaubt, ein fehlendes Feld zählt als 0. Jede Katalogadresse hat ihren eigenen Zähler. Geprüft wird nur, wo eine Signatur es belegt: beim Standard-Katalog und bei Katalogen mit festgehaltenem Schlüssel; bei einem unsignierten fremden Katalog nicht (eine erfundene hohe Nummer würde ihn sonst dauerhaft aussperren). Ältere CouchHUD-Versionen (0.5) ignorieren das Feld.
- **Eigene Quellen** dürfen unsigniert sein. Sie sind mit „⚠ unsignierte Quelle“ markiert, und der Installationsdialog weist darauf hin. Eine Signatur mit dem Schlüssel des Projekts ist dort nicht nötig.

Grenzen: Die Signatur beweist, dass der Index vom Schlüsselinhaber stammt, nicht dass das Plugin harmlos ist. Wer den privaten Schlüssel hat, kann alles freigeben. Er liegt nur im Benutzerprofil des Maintainers (DPAPI-geschützt, `%USERPROFILE%\.couchhud\catalog-signing.key`), nie im Repository. Zusätzlich empfohlen: Branch-Protection auf `main` im Katalog-Repo (Pull Requests mit Review, keine Force-Pushes).

Index signieren (PowerShell 7), nachdem er endgültig ist, ohne spätere Änderung von Bytes oder Zeilenenden (im Katalog-Repo `catalog/* -text` in `.gitattributes`):

```powershell
.\scripts\catalog-sign.ps1 -Index .\catalog\index.json   # erhöht `serial` um 1 und erzeugt index.json.sig; legt nie einen Schlüssel an und lehnt einen Schlüssel ab, der nicht der in CouchHUD eingebaute ist (nur `-GenerateKey` erzeugt einen; die Umsetzung steckt in sign-file.ps1, das auch die Release-Prüfsummen signiert). Signiert werden `couchhud-catalog-v1` + Zeilenumbruch + die Bytes der Datei; dieselbe Signatur gilt weder für Release-Prüfsummen noch umgekehrt (Domain-Trennung). Signaturen im alten Format (nur die Bytes) werden nicht mehr akzeptiert: ältere CouchHUD-Versionen lehnen den neu signierten Index ab
```

### Ablauf und Sicherheit

- **Laden**: 10 Sekunden Zeitlimit, höchstens 1 MB. Der letzte erfolgreiche Index jeder Quelle liegt in `%APPDATA%\CouchHud\catalog-cache.json`; ohne Netz zeigt der Katalog diesen Stand („Offline“).
- **Installieren** (A auf einem Eintrag): erst ein Hinweis, dass **Plugins mit deinen Rechten laufen** und nur aus vertrauenswürdigen Quellen kommen sollen, bestätigt mit A. Dann Download (höchstens 200 MB, Abbruch, wenn 60 Sekunden lang keine Daten kommen), **SHA-256-Pflichtprüfung**, danach der gewöhnliche Installer. Der Installer prüft alle Pfade im Archiv (kein `../`, keine absoluten Pfade oder Laufwerke), Größe und Dateianzahl und packt erst in einen Temp-Ordner neben dem Ziel. Die alte Version wird nur beiseite geschoben und kommt bei einem Fehler zurück. Ein Archiv, das weniger Größe angibt, als es enthält, wird abgelehnt.
- **Offline installieren**: Ist die Quelle nicht erreichbar, zeigt der Katalog den gespeicherten Stand. Installieren ist trotzdem möglich, sobald der Download selbst erreichbar ist. Die Signatur des gespeicherten Standes wird beim Laden erneut geprüft.
- **Nur https**. Katalog, Downloads und Bilder werden über http, UNC-Pfade (`file://server/…`) oder lokale Pfade abgelehnt (außer mit dem Entwickler-Schalter, dann nur lokale Laufwerke und Loopback).
- **Quellen**: Updates kommen nur aus der Quelle, aus der ein Paket installiert wurde. Mitgelieferte und von Hand installierte Plugins sind nur aus der Standardquelle aktualisierbar. Gleiche Ordner in mehreren Quellen: die erste Quelle gewinnt. Dieselbe `id` mit anderem Ordner in einer späteren Quelle ist ein Konflikt und nicht installierbar.
- **Mitgelieferte Plugins**: Eine Kopie in `%APPDATA%\CouchHud\plugins` ersetzt das mitgelieferte Plugin nur, wenn sie eine DLL enthält und ihre Version in `plugin.json` nicht älter ist. Sonst lädt das mitgelieferte, und das Log sagt warum.
- **Neustart**: Plugin-DLLs lassen sich bei laufendem CouchHUD weder entladen noch ersetzen. Neue Plugins werden sofort entpackt und beim nächsten Start geladen. Ein **Update** wird abgelegt (`%APPDATA%\CouchHud\pending`) und beim nächsten Start eingespielt, bevor Plugins geladen werden. Skins sind sofort nutzbar. Ein Update eines mitgelieferten Plugins (z.B. Wand) legt eine Kopie in `%APPDATA%\CouchHud\plugins` an, die dann die mitgelieferte ersetzt.
- **Update-Hinweis**: Fünf Minuten nach dem Start und dann alle 12 Stunden prüft CouchHUD im Hintergrund die Kataloge, nie während ein Spiel läuft. Neue Updates erscheinen einmalig in den Benachrichtigungen und als Abschnitt *Plugins & Skins* auf der Updates-Seite. Abschalten: `"CatalogUpdateCheck": false`.
- Installiert wurde was? `%APPDATA%\CouchHud\catalog-installed.json` merkt sich Version und Ordner (Skins haben keine eigene Versionsangabe).

### Katalog testen (Entwickler-Schalter)

Mit `"CatalogAllowLocal": true` in der `config.json` akzeptiert CouchHUD zusätzlich `file:///…`-Quellen und `http://127.0.0.1`/`localhost` für Katalog und Downloads. Die Prüfsumme bleibt Pflicht. Nur zum Testen einschalten, Standard ist `false`:

```json
"CatalogAllowLocal": true,
"CatalogSources": [ "file:///C:/Temp/katalog/index.json" ]
```

### Eigenen Katalog betreiben

Ein GitHub-Repository mit einer `index.json` reicht (`raw.githubusercontent.com`). Die `.zip`-Dateien hängen als Release-Assets daran. Pull Requests für den Standard-Katalog sind willkommen: Eintrag in `index.json`, Quellcode des Plugins öffentlich verlinken.
