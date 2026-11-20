# Assets

The game draws its own figures and borrows its paper, its colours and its two typefaces from elsewhere

## Drawn here

`wwwroot/actors.svg` holds every figure: the keep, the truck, the slime, the giant slime, the slime with glasses, the bolt tower, the rune tower, the fence and the coin. They follow the DDLC palette and carry no third-party artwork

## Typefaces

- **Nunito** and the Noto families behind "Mya Nunito" — SIL Open Font License 1.1, `wwwroot/vendor/ddlc/MyaNunito-LICENSE.txt`
- **Departure Mono** — SIL Open Font License 1.1, `wwwroot/vendor/ddlc/DepartureMono-LICENSE.txt`

## Palette, web kit and theme

The colours, the interface classes and the fonts arrive from [ddlc-themes](https://github.com/rokokol/ddlc-themes), vendored at commit `fa7be78` under `wwwroot/vendor/ddlc`, MIT licensed

## SignalR client

`wwwroot/signalr.min.js` is the browser build of `@microsoft/signalr` 8.0.7, MIT licensed, `wwwroot/SignalR-LICENSE.txt`
