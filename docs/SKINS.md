# Skins für CouchHUD

**Deutsch** | [English](SKINS.en.md)

Ein Skin ist ein Stylesheet plus optional eigene Button-Symbole. Er ändert Farben, Schriften, Formen, Abstände, Animationen, wie du willst.

## Aufbau

```
%APPDATA%\CouchHud\skins\synthwave\
  skin.json
  skin.css
  (Schriften, Bilder …)
```

**skin.json**

```json
{
  "name": { "de": "Synthwave", "en": "Synthwave" },
  "description": { "de": "Neon, Sonnenuntergang, 1986", "en": "Neon, sunset, 1986" },
  "glyphs": { "A": "✕", "B": "○", "X": "□", "Y": "△", "LB": "L1", "RB": "R1", "≡": "MENU" },
  "soundSet": "nova",
  "sounds": { "move": "move.ogg", "select": "select.ogg", "back": "back.ogg", "notify": "notify.ogg" }
}
```

`name` und `description` sind entweder einfacher Text oder ein Objekt mit `de` und `en`; CouchHUD zeigt den Text der aktuellen Sprache (fehlt er, den anderen). Namen haben höchstens 40 Zeichen, Beschreibungen 300; Steuerzeichen, unsichtbare Formatzeichen (etwa Umkehr der Schreibrichtung oder Zeichen ohne Breite) und `<` `>` werden entfernt, ein Name ist also immer einfacher Text. Ein Skin ohne Namen trägt seinen Ordnernamen. `soundSet` ist optional: eines der Klangsets der App (`default`, `deepsea`, `greenlight`, `neoncity`, `nova`), das spielt, solange der Skin aktiv ist; alles andere wird ignoriert.

`glyphs` ist optional (höchstens 32 Einträge, jede Beschriftung höchstens 12 Zeichen) und nur Gestaltung. Der Schlüssel `≡` ist die Beschriftung der Menütaste (zum Beispiel `☰` oder `MENU`): Welche Symbole erscheinen, bestimmt der Controller (bzw. *Einstellungen › Controller › Tastensymbole*). Ein Eintrag wie `✕`, `○`, `□`, `△`, `L1`, `R1`, `L2`, `R2`, `L3`, `R3`, `OPTIONS`, `CREATE`, `SHARE` oder `PS` gilt nur mit PlayStation-Symbolen, jeder andere nur mit Xbox-Symbolen; sonst zeigt CouchHUD das Standardsymbol der Controller-Art. Die Farben der Tasten bei PlayStation-Symbolen hängen an `:root[data-pad="playstation"]`. Wer für beide Arten eigene Beschriftungen will, nimmt die optionalen Listen `glyphsXbox` und `glyphsPlayStation` (gleiche Grenzen, nur Text); ein Eintrag dort gilt genau für diese Art von Controller und geht dem Eintrag in `glyphs` vor, `glyphs` bleibt die Rückfallebene. Rangfolge: Liste der Controller-Art, dann `glyphs` (nach Art gefiltert), dann das Standardsymbol der Art. Ein PlayStation-Controller, den Steam Input oder DS4Windows als virtuellen XInput-Controller weiterreicht, zählt als PlayStation, solange sein nativer Anschluss die Tasten zuerst meldet; sonst zeigt CouchHUD die Xbox-Symbole.

`sounds` ist optional. Mögliche Ereignisse: `move`, `tab`, `select`, `back`, `bump`, `open`, `close`, `notify`, `toggle`, `boot` und `music`. `music` ist ein Hintergrund-Track, der in Schleife läuft, wenn der Nutzer „UI-Sounds und Musik“ gewählt hat; ohne ihn spielt die eingebaute Musik des Skins. Erlaubt sind `.wav`, `.ogg` und `.mp3` aus dem Skin-Ordner. Für fehlende Ereignisse spielt CouchHUD das eingebaute Klangset.

**HDR**: Ist der HDR-Modus aktiv, steht `data-hdr="on"` am Root-Element. Dort kannst du kräftigere Farben setzen, am besten in Display-P3:

```css
@supports (color: color(display-p3 1 1 1)) {
  :root[data-skin="user:synthwave"][data-hdr] {
    --accent: color(display-p3 1 0.1 0.85);
  }
}
```

**skin.css**: Alle Regeln gelten nur, wenn dein Skin aktiv ist:

```css
:root[data-skin="user:synthwave"] {
  --bg: #12001f;
  --fg: #ffe9ff;
  --muted: rgba(255, 233, 255, 0.6);
  --accent: #ff2bd6;
  --accent-glow: rgba(255, 43, 214, 0.55);
  --panel: rgba(40, 0, 60, 0.7);
  --font: "Orbitron", system-ui, sans-serif;
  --tile-radius: 0.2rem;
  --shelf-tile: 13rem;
  --focus-scale: 1.1;
}

[data-skin="user:synthwave"] .tile.focused {
  box-shadow: 0 0 0 0.15rem #fff, 0 0 2.5rem var(--accent-glow);
}
```

Eigene Schriften und Bilder legst du in den Skin-Ordner und bindest sie relativ ein (`url("orbitron.woff2")`).

**Nur lokale Dateien:** CouchHUD ersetzt Internet-Adressen (`http://`, `https://`, `//host/…`) in `skin.css` durch `about:blank`, bevor es das Stylesheet ausliefert. Ein Skin soll nicht bei jedem Bild bei einem fremden Server anfragen können und ihm so verraten, wann jemand auf der Couch sitzt. Bilder, Schriften und Klänge gehören deshalb in den Skin-Ordner (oder als `data:`-Adresse ins CSS). Der Skin-Ordner darf nur auf lokale Laufwerke zeigen, nie auf Netzlaufwerke oder Freigaben (`\\server\…`). Beim Installieren per `.zip` weist CouchHUD darauf hin, dass ein Skin Inhalte laden kann.

## Wichtige Variablen

| Variable | Wirkung |
|---|---|
| `--bg`, `--fg`, `--muted` | Hintergrund, Text, gedämpfter Text |
| `--accent`, `--accent-glow`, `--accent-contrast` | Akzentfarbe, ihr Leuchten, Textfarbe auf dem Akzent |
| `--panel` | Hintergrund von Karten, Listen, Menüs |
| `--font` | Schriftfamilie |
| `--tile-radius`, `--shelf-tile`, `--focus-scale` | Ecken, Kachelgröße auf Home, Zoom des Fokus |

Für alles andere kannst du die Selektoren aus `ui/styles.css` überschreiben. Die Skins im Katalog (Tiefsee, Grünlicht, Neonstadt, Nova, Sternenstaub) zeigen, wie weit das geht: Tiefsee dreht z.B. das ganze Home-Layout um. Nur der CouchHUD-Skin gehört zum Programm (`ui/skins/`); alle anderen sind Skins wie deiner, im Skin-Ordner installiert, und ihre Regeln beginnen mit `:root[data-skin="user:<Ordner>"]`.

## Die Tab-Leiste

Ein Skin darf die Tabs gestalten (Schrift, Farben, Abstände, den Strich unter dem aktiven Tab, ein „Pill“-Polster um die Liste), aber keinen Tab wegnehmen: Die Tabs der Plugins (zum Beispiel „Smart Home“) stehen in derselben Liste `#tab-list`, und welche Tabs es gibt, bestimmen CouchHUD und die Plugins, nicht der Skin. Die Leiste passt sich selbst an (core/nav.js): Passen alle Tabs, bleiben sie, wie sie sind; sonst werden zuerst die Abstände enger, dann die breiten Tabs schmaler (nie unter die Mindestbreite), und reicht auch das nicht, scrollt die Leiste mit Pfeilen an den Rändern.

- `--tab-min-width` (auf `:root[data-skin="…"]`): die kleinste Breite eines Tabs, der schrumpfen muss, als Zahl mit `rem` oder `px`, zum Beispiel `7rem`. Erlaubt sind 4 bis 10 rem; alles andere (auch ungültige Werte) ist der Standard 6,5 rem.
- Das Programm setzt Sichtbarkeit, Deckkraft, Transformationen, Filter, Zeigerereignisse und eine Schriftgröße von mindestens 0,75 rem für Tabs, Pfeile und die Leiste selbst zur Laufzeit mit `!important` am Element. Ein Skin kann Tabs also nicht ausblenden, verkleinern oder überdecken; der Versuch bleibt wirkungslos.
- `scripts/skin-check.mjs` (im CouchHUD-Repository; `scripts/catalog-entry.ps1` ruft es beim Packen eines Skin-Ordners auf) ist eine **Prüfhilfe für die Katalog-Durchsicht**, kein Schutz: Es meldet Regeln, die die Tab-Leiste ausblenden (`display:none`, `visibility:hidden`, Breite 0, `nth-child` und Ähnliches), auch mit CSS-Escapes oder `[id=…]`-Selektoren. Es findet die ehrlichen Fehler und die plumpen Versuche; durchgesetzt wird die Sichtbarkeit zur Laufzeit.

## Quadratische Kacheln

Ein Skin mit quadratischen Kacheln (zum Beispiel für ein PlayStation-Aussehen) setzt `--tile-art: square;` (auf `:root[data-skin="…"]`, nur dort wird sie gelesen). Dann zeigen die Kacheln in Bibliothek, Favoriten und auf Home das **Quadrat-Bild** der Spiele (von SteamGridDB: quadratische Grids, danach Icons; Steam selbst hat keins). Hat ein Eintrag kein Quadrat-Bild, zeigt die Kachel das Cover mittig auf ein Quadrat zugeschnitten (`background-size: cover`, Klasse `art-cropped` an `.art.art-square`), in jedem Skin gleich. Ohne die Variable (oder mit jedem anderen Wert) bleibt es beim Cover, der Standard-Skin ändert sich nicht. Die Form der Kachel selbst bestimmt der Skin, zum Beispiel mit `aspect-ratio: 1 / 1`. Das Quadrat-Bild wählt der Nutzer unter *Artwork ändern* im Reiter „Quadrat“.

```css
:root[data-skin="user:synthwave"] {
  --tile-art: square;
}
:root[data-skin="user:synthwave"] .tile .art.art-square { aspect-ratio: 1 / 1; }
```

## Mauszeiger

Bewegt der Nutzer eine Maus, zeichnet CouchHUD einen eigenen Zeiger. Er kommt aus dem Skin: Jeder Skin darf ihn setzen, ein Skin ohne eigenen Zeiger zeigt automatisch den Standard-Pfeil von CouchHUD. Mit der Einstellung „Echter Mauszeiger“ (Einstellungen › Darstellung) zeigt der Nutzer stattdessen den normalen Windows-Zeiger, dann greift dein Zeiger nicht.

| Variable | Wirkung |
|---|---|
| `--pointer` | Bild des Zeigers, am besten ein kleines SVG als `data:`-Adresse: `url("data:image/svg+xml,…")` (`#` als `%23` schreiben) |
| `--pointer-size` | Kantenlänge des quadratischen Bildes, Standard `4.6rem` |
| `--pointer-hx`, `--pointer-hy` | Hotspot als Bruchteil der Größe, `0 0` ist die linke obere Ecke, `0.5 0.5` die Mitte. Genau dort sitzt die Mausposition |

```css
:root[data-skin="user:synthwave"] {
  --pointer: url("data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'><path d='M5 3 L5 35 L13 27.5 L19 41 L25.5 38.2 L19.5 25 L31 25 Z' fill='%23ff2bd6' stroke='%2312141c' stroke-width='3.2' stroke-linejoin='round'/></svg>");
  --pointer-hx: 0.104;  /* Spitze des Pfeils bei (5, 3) von 48 */
  --pointer-hy: 0.0625;
}
```

Tipps: Ein Zeiger muss auf dem Fernseher aus dem Sessel auffallen. Eine dunkle Kontur (hier `#12141c`) hält ihn auf hellem Hintergrund sichtbar, eine helle Füllung auf dunklem. Der Zeiger ruht, solange die Maus still steht, bitte keine Endlos-Animation. Wer den Zeiger per CSS selbst zeichnen will, kann `#cursor .cp` direkt stylen (`.click` am `#cursor` liegt kurz beim Klick an) und bei `:root[data-motion="off"]` auf Bewegung verzichten, das ist die Einstellung „Animationen reduzieren“. Ein relativer Pfad in `--pointer` ist nur zuverlässig, wenn du ihn direkt als `background-image` von `#cursor .cp` setzt.

Das ist nur der Zeiger innerhalb von CouchHUD. In anderen Fenstern bleibt es der Windows-Zeiger.

## Ausprobieren

CouchHUD mit `--dev` starten, unter *Einstellungen › Darstellung › Skin* deinen Skin wählen. Änderungen an der CSS siehst du nach erneutem Auswählen.

Ohne Windows geht es auch: `node scripts/preview-server.js` startet die Oberfläche mit Beispieldaten im Browser. Eigene Skin-Ordner dort mit `CATALOG_SKINS=<Ordner mit deinen Skin-Ordnern> node scripts/preview-server.js` ausliefern und einen Skin mit `node scripts/ui-shot.mjs --catalog <Ordner> skin:user:<Ordnername> shot:home` ansehen. Das Stylesheet wird dort von Internet-Adressen befreit, genau wie in der App.

## Teilen

Ordner als `.zip` packen (mit oder ohne übergeordneten Ordner), auf GitHub mit dem Topic `couchhud-skin` veröffentlichen. Andere installieren ihn in CouchHUD unter *Einstellungen › Darstellung › Neue Skins installieren*.

Für den Katalog: Gib dem Skin einen eigenen Namen, keine fremde Marke (kein Konsolen-, Hersteller- oder Spielename), und verwende keine fremden Logos. Ein Stil darf an etwas erinnern, aber nicht danach heißen.
