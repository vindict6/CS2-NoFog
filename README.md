# CS2-NoFog

A [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin for Counter-Strike 2 that removes fog on any map when an admin types `!nofog` in chat.

## Features

- `!nofog` (or `/nofog`, or `css_nofog` from console) toggles fog removal on the current map
- Disables all fog sources: `env_fog_controller`, `env_gradient_fog`, and `env_cubemap_fog`
- Typing `!nofog` again restores the map's original fog settings
- Automatically re-applies after round restarts
- Resets to map defaults on map change

## Requirements

- Counter-Strike 2 dedicated server with [Metamod:Source](https://www.sourcemm.net/downloads.php?branch=master)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/releases) (API version 200+)

## Installation

1. Download the latest release zip from the [Releases](../../releases) page.
2. Extract it into your server's `game/csgo/` directory so the plugin ends up at:
   ```
   game/csgo/addons/counterstrikesharp/plugins/CS2-NoFog/CS2-NoFog.dll
   ```
3. Restart the server or run `css_plugins load CS2-NoFog`.

## Usage

| Command | Where | Description |
|---------|-------|-------------|
| `!nofog` / `/nofog` | Chat | Toggle fog removal on the current map |
| `css_nofog` | Client/server console | Same as above |

## Permissions

The command requires the `@css/generic` admin flag (configured in `addons/counterstrikesharp/configs/admins.json`).

## Building from source

```
dotnet build -c Release
```

The compiled plugin will be at `bin/Release/net8.0/CS2-NoFog.dll`.

## License

[MIT](LICENSE)
