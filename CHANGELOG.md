# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html)

## Unreleased

## [0.1.0] - 2026-11-21

The first release: a two-player tower defence in the browser, with accounts, friends, live invitations and a match archive

### Added

- A duel between one defender and one attacker on a maze generated per room, with five waves and a keep that falls at zero hearts
- Four towers, a mine, spikes and a fence, each with its own price, reach and rate of fire; the server resolves every wave and the browser replays the frames
- Slime groups queued per entrance, an armored slime that takes half damage, a giant, a ranged slime and an APC that carries five passengers past a broken fence
- One plain slime that picks a branch at a fork, and a slime with glasses that buys the shortest way for twice the gold
- A per-wave budget that replaces rather than adds, with a refund only for structures bought in the current wave
- The defender's plan stays hidden from the attacker until the wave is resolved, and both sides see the same cells a tower or a trap covers
- Accounts, friends confirmed by the other side, live invitations and a side swap that both players agree to
- Guest play through the invite link, with no registration: the server hands that browser a seat of its own
- Online players with an invitation straight from the list, and random matchmaking that seats two players who are both on the board
- One active duel per account, and a duel closes for both sides when either leaves or stays away too long
- A match archive with the exact battle frames: any finished match replays without an account, wave by wave
- A restart marks a duel that was running as interrupted, says why and offers a new one
- A guide screen built from the same rules the fight uses, so a price, a reach or an interval cannot disagree with the game
- The interface in Russian and English, a dark and a light theme, and a sound control with an effect for a siege, a mine and an APC on the move
- Docker Compose deployment behind nginx, with a Let's Encrypt certificate and strict TLS from Cloudflare to the origin

The project is released under the MIT licence, see [LICENSE](LICENSE)

[0.1.0]: https://github.com/rokokol/truck-kun-tower/releases/tag/v0.1.0
