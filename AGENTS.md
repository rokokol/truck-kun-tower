# Truck-kun Tower

Two players share one maze: one defends the keep, the other buys slimes and picks the gates they come from

## Run it

- `cp .env.example .env`, fill `POSTGRES_PASSWORD` and `JWT_KEY`, then `docker compose up -d --build` serves the game on <http://localhost:5184>
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

## Where a rule lives

- `Duel.cs` owns prices, waves, ownership limits and the outcome; the browser only replays the frames the server sends, so a rule written in `game.js` alone is not a rule
- The server closes a wave when its thirty-second clock runs out, so no browser timer decides anything
- A finished match reaches PostgreSQL after the room lock is released, never inside it
- `wwwroot/actors.svg` takes its colours from DDLC theme variables; a page that draws those sprites without the variables shows black silhouettes

## Data and identity

- Entities, the DbContext and the model live in `Data/`; services read and write through `IUnitOfWork` and the repositories, never through `DuelDb`
- Migrations sit in `Migrations/` and run at startup. A new one is generated in the SDK image, because the host has no .NET SDK:
  `docker run --rm --network=host -v "$PWD:/src" -v nuget-cache:/root/.nuget/packages -w /src mcr.microsoft.com/dotnet/sdk:8.0 bash -c 'dotnet tool install -g dotnet-ef --version 8.0.8 >/dev/null 2>&1; export PATH=$PATH:/root/.dotnet/tools; dotnet ef migrations add <Name> --output-dir Migrations'`
- An account holds a nickname and a PBKDF2 password hash; the JWT carries the id for seven days. `Jwt__Key` is required in a deployment, otherwise each restart signs everybody out
- A chair in a live board belongs to an account or to a guest holding the random seat token; the guest path keeps the plain invite link working without a registration
- Accounts also get friends, invitations and match history; a purely guest room lives only in memory, so a restart marks its `Room` row abandoned

## Web app

- Screens: `#auth` and `#lobby` are Alpine components in `wwwroot/lobby.js`, the board stays the hand-drawn SVG of `wwwroot/game.js`. Neither script draws the other's screen: they meet on the `tower:screen` event
- The invite is a plain link with `?room=`; nothing depends on a messenger or a native shell
- The hub at `/bell` carries the room state, friend requests, invitations, swap requests and presence. The JWT travels as `access_token` in the query, the guest seat token as the second argument of `JoinRoom`
- The game answers both plain HTTP behind the proxy and HTTPS on `8443`; keep both in mind when a change touches URLs or cookies

## Deploy

- `deploy/vps/` carries the production compose and the nginx site for `tower.rokokol.art`; port 443 on that host belongs to the VPN, so the site listens on 80 and Cloudflare terminates visitor TLS with the zone in Flexible mode
- The game container runs with `network_mode: host` and binds `127.0.0.1:5184`; PostgreSQL publishes only `127.0.0.1:5432` and keeps its data in the `tower-db` volume
- The production `.env` holds `POSTGRES_PASSWORD` and `JWT_KEY`; it never reaches git

## Assets

- `docs/` holds what the README and the old bot profile needed: a 640×360 banner, a 640×360 demo GIF and a 512×512 avatar. They are not game sources
- `ASSETS.md` names every borrowed font and the vendored theme
