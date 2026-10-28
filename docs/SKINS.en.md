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
  "name": "Synthwave",
  "description": "Neon, sunset, 1986",
  "glyphs": { "A": "✕", "B": "○", "X": "□", "Y": "△", "LB": "L1", "RB": "R1" },
  "sounds": { "move": "move.ogg", "select": "select.ogg", "back": "back.ogg", "notify": "notify.ogg" }
}
```

`glyphs` is optional and only styling: which symbols appear is decided by the controller (or *Settings › Controller › Button symbols*). An entry such as `✕`, `○`, `□`, `△`, `L1`, `R1`, `L2`, `R2`, `L3`, `R3`, `OPTIONS`, `CREATE`, `SHARE` or `PS` only applies with PlayStation symbols, any other only with Xbox symbols; otherwise CouchHUD shows the default symbol of that kind of controller. The button colors with PlayStation symbols hang on `:root[data-pad="playstation"]`.

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

For everything else you can override the selectors from `ui/styles.css`. The built-in skins in `ui/skins/` (Deep Sea, Greenlight, Neon City, Nova) show how far that goes: Deep Sea (`deepsea.css`), for example, turns the whole Home layout around.

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

It also works without Windows: `node scripts/preview-server.js` starts the interface with sample data in the browser. There, simply add your skin as another file in `ui/skins/` and register it in `ui/skins/index.js`.

## Sharing

Pack the folder as a `.zip` (with or without the parent folder) and publish it on GitHub with the topic `couchhud-skin`. Others install it in CouchHUD under *Settings › Look › Install new skins*.

For the catalog: give the skin a name of its own, not someone else's brand (no console, manufacturer or game name), and use no third-party logos. A style may remind you of something, but must not be named after it.
