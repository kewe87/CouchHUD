# Skins for CouchHUD

[Deutsch](SKINS.md) | **English**

A skin is a stylesheet plus optional button glyphs of its own. It changes colors, fonts, shapes, spacing and animations, however you like.

## Structure

```
%APPDATA%\CouchHud\skins\synthwave\
  skin.json
  skin.css
  (fonts, images …)
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

`name` and `description` are either plain text or an object with `de` and `en`; CouchHUD shows the one for the current language (the other one if it is missing). Names have at most 40 characters, descriptions 300; control characters, invisible format characters (such as text-direction overrides or zero-width characters) and `<` `>` are removed, so a name is always plain text. A skin without a name carries its folder name. `soundSet` is optional: one of the sound sets of the app (`default`, `deepsea`, `greenlight`, `neoncity`, `nova`) that plays while the skin is active; anything else is ignored.

`glyphs` is optional (at most 32 entries, each label at most 12 characters) and only styling. The key `≡` is the label of the menu button (for example `☰` or `MENU`): which symbols appear is decided by the controller (or *Settings › Controller › Button symbols*). An entry such as `✕`, `○`, `□`, `△`, `L1`, `R1`, `L2`, `R2`, `L3`, `R3`, `OPTIONS`, `CREATE`, `SHARE` or `PS` only applies with PlayStation symbols, any other only with Xbox symbols; otherwise CouchHUD shows the default symbol of that kind of controller. The button colors with PlayStation symbols hang on `:root[data-pad="playstation"]`. For own labels for both kinds use the optional lists `glyphsXbox` and `glyphsPlayStation` (same limits, text only); an entry there applies to exactly that kind of controller and wins over the entry in `glyphs`, which stays the fallback. Order: the list of the controller kind, then `glyphs` (filtered by kind), then the default symbol of the kind. A PlayStation pad that Steam Input or DS4Windows passes on as a virtual XInput pad counts as PlayStation as long as its native connection reports the presses first; otherwise CouchHUD shows the Xbox symbols.

`sounds` is optional. Possible events: `move`, `tab`, `select`, `back`, `bump`, `open`, `close`, `notify`, `toggle`, `boot` and `music`. `music` is a background track that loops when the user has chosen "Menu sounds and music"; without it, the skin's built-in music plays. Allowed are `.wav`, `.ogg` and `.mp3` from the skin folder. For missing events CouchHUD plays the built-in sound set.

**HDR**: when HDR mode is active, the root element carries `data-hdr="on"`. There you can set stronger colors, ideally in Display P3:

```css
@supports (color: color(display-p3 1 1 1)) {
  :root[data-skin="user:synthwave"][data-hdr] {
    --accent: color(display-p3 1 0.1 0.85);
  }
}
```

**skin.css**: all rules only apply while your skin is active:

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

Put your own fonts and images in the skin folder and reference them relatively (`url("orbitron.woff2")`).

**Local files only:** CouchHUD replaces internet addresses (`http://`, `https://`, `//host/…`) in `skin.css` with `about:blank` before it serves the stylesheet. A skin should not be able to contact a foreign server for every image and so tell it when someone is sitting on the couch. Images, fonts and sounds therefore belong in the skin folder (or in the CSS as a `data:` address). The skin folder may only point to local drives, never to network drives or shares (`\\server\…`). When installing from a `.zip`, CouchHUD points out that a skin can load content.

## Important variables

| Variable | Effect |
|---|---|
| `--bg`, `--fg`, `--muted` | Background, text, muted text |
| `--accent`, `--accent-glow`, `--accent-contrast` | Accent color, its glow, text color on the accent |
| `--panel` | Background of cards, lists, menus |
| `--font` | Font family |
| `--tile-radius`, `--shelf-tile`, `--focus-scale` | Corners, tile size on Home, zoom of the focus |

For everything else you can override the selectors from `ui/styles.css`. The skins in the catalog (Deep Sea, Greenlight, Neon City, Nova, Stardust) show how far that goes: Deep Sea, for example, turns the whole Home layout around. Only the CouchHUD skin is part of the app (`ui/skins/`); all others are skins like yours, installed into the skins folder, and their rules begin with `:root[data-skin="user:<folder>"]`.

## The tab bar

A skin may style the tabs (type, colours, gaps, the line under the active tab, a "pill" padding around the list) but must not take a tab away: the plugins' tabs (for example "Smart Home") stand in the same list `#tab-list`, and which tabs exist is decided by CouchHUD and the plugins, not by the skin. The bar fits itself (core/nav.js): when all tabs fit they stay as they are; otherwise the gaps get tighter first, then the wide tabs narrower (never below the minimum width), and when even that is not enough the bar scrolls with arrows at its edges.

- `--tab-min-width` (on `:root[data-skin="…"]`): the smallest width of a tab that has to shrink, as a number with `rem` or `px`, for example `7rem`. 4 to 10 rem is allowed; anything else (invalid values too) is the default of 6.5 rem.
- At runtime the program sets visibility, opacity, transforms, filters, pointer events and a font size of at least 0.75 rem for the tabs, the arrows and the bar itself inline with `!important`. A skin cannot hide, shrink or cover tabs; the attempt has no effect.
- `scripts/skin-check.mjs` (in the CouchHUD repository; `scripts/catalog-entry.ps1` calls it when it packs a skin folder) is a **lint for the catalog review**, not a protection: it reports rules that hide the tab bar (`display:none`, `visibility:hidden`, zero width, `nth-child` and the like), also with CSS escapes or `[id=…]` selectors. It finds the honest mistakes and the plain attempts; the visibility is enforced at runtime.

## Square tiles

A skin with square tiles (a PlayStation look, say) sets `--tile-art: square;` (on `:root[data-skin="…"]`; it is only read there). The tiles in the library, favorites and on Home then show the games' **square picture** (from SteamGridDB: square grids, then icons; Steam itself has none). An entry without a square picture shows its cover cropped to a square, centred (`background-size: cover`, class `art-cropped` on `.art.art-square`), the same in every skin. Without the variable (or with any other value) the cover stays, so the default skin does not change. The shape of the tile itself is up to the skin, for example `aspect-ratio: 1 / 1`. Users choose the square picture under *Change artwork* in the "Square" tab.

```css
:root[data-skin="user:synthwave"] {
  --tile-art: square;
}
:root[data-skin="user:synthwave"] .tile .art.art-square { aspect-ratio: 1 / 1; }
```

## Mouse pointer

When the user moves a mouse, CouchHUD draws its own pointer. It comes from the skin: every skin may set it, and a skin without a pointer of its own automatically shows CouchHUD's default arrow. With the setting "Real mouse pointer" (*Settings › Look*) the user sees the normal Windows pointer instead, and your pointer does not apply.

| Variable | Effect |
|---|---|
| `--pointer` | Image of the pointer, ideally a small SVG as a `data:` address: `url("data:image/svg+xml,…")` (write `#` as `%23`) |
| `--pointer-size` | Edge length of the square image, default `4.6rem` |
| `--pointer-hx`, `--pointer-hy` | Hotspot as a fraction of the size; `0 0` is the top left corner, `0.5 0.5` the center. The mouse position sits exactly there |

```css
:root[data-skin="user:synthwave"] {
  --pointer: url("data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'><path d='M5 3 L5 35 L13 27.5 L19 41 L25.5 38.2 L19.5 25 L31 25 Z' fill='%23ff2bd6' stroke='%2312141c' stroke-width='3.2' stroke-linejoin='round'/></svg>");
  --pointer-hx: 0.104;  /* tip of the arrow at (5, 3) of 48 */
  --pointer-hy: 0.0625;
}
```

Tips: a pointer has to stand out on the TV from the armchair. A dark outline (here `#12141c`) keeps it visible on a light background, a light fill on a dark one. The pointer rests while the mouse stands still, so please no endless animation. If you want to draw the pointer yourself in CSS, you can style `#cursor .cp` directly (`.click` sits on `#cursor` briefly during a click) and do without motion under `:root[data-motion="off"]`, which is the setting "Reduce animations". A relative path in `--pointer` is only reliable if you set it directly as the `background-image` of `#cursor .cp`.

This is only the pointer inside CouchHUD. In other windows it stays the Windows pointer.

## Trying it out

Start CouchHUD with `--dev` and pick your skin under *Settings › Look › Skin*. You see changes to the CSS after selecting it again.

It also works without Windows: `node scripts/preview-server.js` starts the interface with sample data in the browser. There, serve your skin folders with `CATALOG_SKINS=<folder with your skin folders> node scripts/preview-server.js` and look at a skin with `node scripts/ui-shot.mjs --catalog <folder> skin:user:<folder name> shot:home`. The stylesheet is cleaned of web addresses there just as the app does it.

## Sharing

Pack the folder as a `.zip` (with or without the parent folder) and publish it on GitHub with the topic `couchhud-skin`. Others install it in CouchHUD under *Settings › Look › Install new skins*.

For the catalog: give the skin a name of its own, not someone else's brand (no console, manufacturer or game name), and use no third-party logos. A style may remind you of something, but must not be named after it.
