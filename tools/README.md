# tools

The other scripts in this directory draw the rest of `docs/`, and each one explains itself in its own header

## Demo GIF

`docs/telegram-demo.gif` has one recipe, and it lives here

```sh
node tools/demo-gif.mjs
```

The run needs a game it can reach, Playwright, Chromium and ffmpeg with ffprobe. It records the live site `https://tower.rokokol.art:8443` by default, and `--url` names another one, such as a local `http://127.0.0.1:5184`

`PLAYWRIGHT_CORE` and `CHROMIUM` take the module and the browser, the same way the scripts in `tests/` do, and a bare `chromium` is looked up on `PATH`

The script plays three empty waves with two guest seats and no account, because the attacker needs the gold they leave behind: one order of every unit costs more than the first wave can pay. The fourth wave then carries one order of each type at the gates the maze offers, and the defender meets it with a bolt, a pulse and a frost tower, a fence and spikes, placed where every road meets before the keep

It records that replay and composes the frame itself, with a brand panel on the left and the board with its status line and wave pips on the right, so every run lays the same frame out the same way

A run fails with a non-zero exit and one line naming the cause when a tower cannot be bought, when the page never starts a replay, when the wave is too long to hold its pace in a 9-10 s loop, or when ffprobe reports a size other than 640×360. The GIF in `docs/` is left as it was, because the new one only replaces it after it passes

| Option | Effect |
| --- | --- |
| `--url <url>` | record another game |
| `--out <file>` | write the GIF elsewhere |
| `--frames <dir>` | keep the PNG frames in a new, empty directory |
| `--keep-frames` | keep the frames in a temporary directory and print its path |

## Banner and link preview

`docs/telegram-banner.png` (640×360) and `docs/og.png` (1200×630) both come from one artwork,
`docs/truck-kun-tower.jpg`

```sh
node tools/banner.mjs
```

The artwork is 2:1 and both frames are wider than tall, so the picture is fitted to the frame's
width and the rest of the frame takes the artwork's own paper colour, read from its top-left pixel.
A crop would cut the drawn wordmark or the keep, and a stretch would show a longer truck than the
drawing has

The script writes `docs/og.png` and copies the same bytes to `wwwroot/og.png`, the file
`wwwroot/index.html` names in `og:image`, so the served copy never drifts from the one `docs/`
keeps

A run fails when the artwork is missing, when a frame cannot hold the fitted picture, when
ImageMagick writes another size, or when the served copy differs from the source by a byte

## Icons

```sh
node tools/icons.mjs
```

The favicon set is drawn from `docs/logo.png`: `wwwroot/favicon.ico` with 16, 32 and 48 px frames, `wwwroot/icon-192.png`, `wwwroot/icon-512.png` and `wwwroot/apple-touch-icon.png`. Each frame keeps the artwork's alpha and one clear pixel of air around the mark

The mark of the lockup is 71×119, which cannot fill a 512 px icon, so the set comes from `docs/logo.svg` rendered at four times the lockup. Every run proves the two files carry the same artwork: the vector drawn at the PNG's own size has to match the PNG's mark within 3%

A run fails when the mark's ink box no longer touches all four sides of the box measured from the PNG, when the 16 px frame leaves less than a pixel of air, or when a written file comes out at another size
