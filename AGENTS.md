# Truck-kun Tower

Two players share one maze: one defends the keep, the other buys slimes and picks the gates they come from

## Run it

- `cp .env.example .env`, fill `POSTGRES_PASSWORD` and `JWT_KEY`, then `docker build --network=host -t truck-kun-tower . && docker compose up -d --no-build` serves the game on <http://localhost:5184>
- The build is a separate step because Compose 5 refuses the `network: host` the build section asks for, and NuGet restore inside the bridge fails when the host DNS goes through a VPN
- `docker compose up -d db` alone is enough when the game runs from the SDK: `dotnet run --urls http://localhost:5184`
- Do not bring back a Nix flake: `nix build` stopped working as soon as EF Core arrived, because the sandbox restore needs a pinned NuGet set, so Docker replaced it

## Checks

- `node check.mjs` and `node balance.mjs` need a running server; point them elsewhere with `URL=http://localhost:5184 node check.mjs`
- `check.mjs` is a full pass through the public API: accounts, friends, invitations, sides, the rules of the game, the history and the leaderboard
- Unit tests live in `tests/TruckKun.Tower.Tests` and mock the repositories with Moq:

```sh
docker run --rm --network=host -v "$PWD:/src" -v nuget-cache:/root/.nuget/packages -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet test tests/TruckKun.Tower.Tests -p:ArtifactsPath=/tmp/art
```

- A red `check.mjs` run against an older container is expected, because the script pins the current wire format
- Run `node tests/ui-boot.mjs`, `node tests/ui-preferences.mjs` and `node tests/ui-replay.mjs` with local `playwright-core` and Chromium. Set `PLAYWRIGHT_CORE` or `CHROMIUM` to override their paths. `ui-boot` loads the real script set, SignalR build included, because the six classic scripts share one global scope and a repeated top-level name stops a script from running; the other two need no game server, SignalR or PostgreSQL

## Where a rule lives

- `Duel.cs` owns prices, waves, ownership limits and the outcome; the browser only replays the frames the server sends, so a rule written in `game.js` alone is not a rule
- The server closes a wave when its clock runs out, so no browser timer decides anything
- A finished match reaches PostgreSQL after the room lock is released, never inside it
- `wwwroot/actors.svg` takes its colours from DDLC theme variables; a page that draws those sprites without the variables shows black silhouettes

## Data and identity

- Entities, the DbContext and the model live in `Data/`; services read and write through `IUnitOfWork` and the repositories, never through `DuelDb`
- Migrations sit in `Migrations/` and run at startup. A new one is generated in the SDK image, because the host has no .NET SDK:
  `docker run --rm --network=host -v "$PWD:/src" -v nuget-cache:/root/.nuget/packages -w /src mcr.microsoft.com/dotnet/sdk:8.0 bash -c 'dotnet tool install -g dotnet-ef --version 8.0.8 >/dev/null 2>&1; export PATH=$PATH:/root/.dotnet/tools; dotnet ef migrations add <Name> --output-dir Migrations'`
- An account holds a nickname and a PBKDF2 password hash; the JWT carries the id for seven days. `Jwt__Key` is required in a deployment, otherwise each restart signs everybody out
- A chair in a live board belongs to an account or to a guest holding the random seat token; the guest path keeps the plain invite link working without a registration
- Accounts also get friends, invitations and match history; a purely guest room lives only in memory, so a restart marks its `Room` row abandoned

## The io-game

- Screens: `#auth` and `#lobby` are Alpine components in `wwwroot/lobby.js`, the board stays the hand-drawn SVG of `wwwroot/game.js`. Neither script draws the other's screen: they meet on the `tower:screen` event
- The invite is a plain link with `?room=`; nothing depends on a messenger or a native shell
- `Dockerfile` replaces `?v=@hash@` in `index.html` with a hash of the marked assets. Cloudflare can keep an asset after its content changes, so the URL must change too
- The hub at `/bell` carries the room state, friend requests, invitations, swap requests and presence. The JWT travels as `access_token` in the query, the guest seat token as the second argument of `JoinRoom`
- Use `https://tower.rokokol.art/` for public links, with no port suffix

## Deploy

- The host runs no .NET SDK, so the image is built here and carried over: `docker build --network=host -t truck-kun-tower .`, then `docker save truck-kun-tower | zstd -3 | ssh <host> 'zstd -d | docker load'`, then `cd /opt/truck-kun-tower && docker compose up -d`
- `rsync -a --exclude .git --exclude bin --exclude obj --exclude .env ./ <host>:/opt/truck-kun-tower/` keeps the sources and the compose file there in step with the repository; the server itself never builds
- One `docker-compose.yml` serves both machines: host networking, the game on `127.0.0.1:5184`, PostgreSQL in the `tower-db` volume. Only the command differs — `up -d --no-build` here after a build, a plain `up -d` there on the image just loaded
- See `deploy/vps/nginx.conf` for the origin TLS port and the Certbot exception. Do not change the VPN listener
- The production `.env` holds `POSTGRES_PASSWORD` and `JWT_KEY`; it never reaches git
- The pre-PostgreSQL history stays untouched in the `vps_tower-data` volume; the game no longer reads it

## Assets

- `docs/` holds what the README and the old bot profile needed: a 640×360 banner, a 640×360 demo GIF and a 512×512 avatar. They are not game sources
- `ASSETS.md` names every borrowed font and the vendored theme
- `docs/truck-kun-tower.jpg` is the one artwork behind `docs/telegram-banner.png` (640×360) and `docs/og.png` (1200×630, the link preview the page names in `og:image`). `node tools/banner.mjs` draws both and copies the preview to `wwwroot/og.png`, which the site serves: the picture is fitted to each frame's width, so a crop cannot cut the drawn wordmark and a stretch cannot lengthen the truck. Both take black in the margin, which frames the picture on a messenger's light and dark ground alike
- `node tools/shots.mjs` plays a real duel and shoots `docs/cover.png`, `docs/planning.png` and `docs/battle.png` in English, with `--lang ru` for the Russian page; `node tools/demo-gif.mjs` records `docs/telegram-demo.gif` the same way and takes the same flag
- `node tools/icons.mjs` draws the favicon set from `docs/logo.png`: `wwwroot/favicon.ico` at 16, 32 and 48, `wwwroot/icon-192.png`, `wwwroot/icon-512.png` and `wwwroot/apple-touch-icon.png`, which `wwwroot/site.webmanifest` and the page's icon links name
