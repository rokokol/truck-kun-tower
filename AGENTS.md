# Truck-kun Tower

Two players share one maze: one defends the keep, the other buys slimes and picks the gates they come from

## Run it

- `docker compose up -d` serves the game on <http://localhost:5184> and keeps it running between sessions
- `dotnet run --urls http://localhost:5184` runs the same code straight from the SDK, which is the faster loop
- Do not bring back a Nix flake: `nix build` stopped working as soon as EF Core arrived, because the sandbox restore needs a pinned NuGet set, so Docker replaced it

## Checks

- `node check.mjs` and `node balance.mjs` need a server that already listens; point them elsewhere with `URL=http://localhost:5184 node check.mjs`
- `check.mjs` asserts on the JSON that `/room/{id}/move` returns, so a change to that shape is a test change in the same commit
- A red run against an older container is expected, because the checks pin the current wire format

## Where a rule lives

- `Duel.cs` owns prices, waves, ownership limits and the outcome; the browser only replays the frames the server sends, so a rule written in `game.js` alone is not a rule
- The server closes a wave when its thirty-second clock runs out, so no browser timer decides anything
- A finished match reaches SQLite after the room lock is released, never inside it
- `wwwroot/actors.svg` takes its colours from DDLC theme variables; a page that draws those sprites without the variables shows black silhouettes

## Telegram

- `TelegramBot.cs` validates `initData` with the bot token and answers `GET /config`; `wwwroot/telegram.js` is the only file that talks to the Telegram SDK, and it falls back to plain storage in a normal browser
- A player is identified by their Telegram id, so the same person keeps their side after the webview reloads; a shared ticket lives under `duel:<room>` in the Mini App storage
- Settings arrive as `Telegram__BotToken`, `Telegram__PublicUrl`, `Telegram__AppLink` and `Telegram__ApiAddress`; the token never reaches the browser

## Deploy

- `deploy/vps/` carries the production compose and the nginx site for `tower.rokokol.art`; port 443 on that host belongs to the VPN, so the site listens on 80 and Cloudflare terminates visitor TLS with the zone in Flexible mode
- The container runs with `network_mode: host` and binds `127.0.0.1:5184`, so only the reverse proxy reaches it

## Assets

- `docs/` holds what BotFather asks for: a 640×360 banner, a 640×360 demo GIF and a 512×512 avatar. They are not game sources
- `ASSETS.md` names every borrowed font and the vendored theme
