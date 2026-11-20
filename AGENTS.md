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

- `Duel.cs` owns prices, waves and the outcome; the browser only replays the frames the server sends, so a rule written in `game.js` alone is not a rule
- A finished match reaches SQLite after the room lock is released, never inside it
- `wwwroot/actors.svg` takes its colours from DDLC theme variables; a page that draws those sprites without the variables shows black silhouettes

## Assets

- `docs/` holds what BotFather asks for: a 640×360 banner, a 640×360 demo GIF and a 512×512 avatar. They are not game sources
- `ASSETS.md` names every borrowed font and the vendored theme
