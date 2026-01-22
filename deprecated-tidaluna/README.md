# I'm a proud hater of the Rust programming language, I'm stopping the development of the TidaLuna plugin.

## Reactivo — Now Playing Audio Visualiser for TidaLuna

>A lightweight audio visualiser plugin for TidaLuna that overlays reactive glow/vignette/pulse effects on the Now Playing view. It receives analysis data from a WebSocket source and animates the UI in response to low-frequency/bass information.

## Features
- Animated glow, vignette and pulse ring synced to audio analysis
- Optional on-screen connection status and numeric stats (BPM, bass, frequency)
- Auto-reconnect to a local or remote WebSocket audio-analysis server
- Minimal styling and non-intrusive overlay (pointer-events disabled)

## Installation


- Download the executable file and store it in a place you can forget or it doesn't bother you.
https://github.com/itzzexcel/luna-plugins/raw/refs/heads/master/plugins/itswickedoutside/src/net9.0-windows.zip
What it will do, is that it will auto register as a start-up application.
- Installation ↪️ QUESTIONS ABOUT IT: DM ME via Discord // @itzzexcel 
- Requirements: 
  - .NET Runtime 9.0
    - https://dotnet.microsoft.com/es-es/download/dotnet/thank-you/runtime-9.0.11-windows-x64-installer for x64 Windows.
- Yes it does waste much RAM

- Install my store.json file to TIDAL:
https://github.com/itzzexcel/luna-plugins/releases/download/latest/store.json
Enable the reactivo  plugin listed in TidaLuna settings

Make sure the plugin can reach an analysis WebSocket at the configured URL (default: `ws://localhost:5343`). The visualiser expects messages as JSON arrays where the first item is an object matching the shape:

```json
[
  {
    "utime": 639020764300711759,
    "analysis": 75,
    "energy": 0.35578167,
    "bass": {
      "average": 3.6769704E-06,
      "max": 9.066105E-06,
      "strongest": {
        "frequency": 117.1875,
        "magnitude": 1.0377037E-05
      }
    },
    "treble": {
      "average": 2.7877405E-07,
      "max": 1.0435621E-06,
      "strongest": {
        "frequency": 4054.6875,
        "magnitude": 4.205432E-06
      }
    },
    "bpm": 0
  }
]
```

## Usage

Once enabled, Reactivo overlays the Now Playing container and attempts a WebSocket connection to the configured address. If a connection is present and messages follow the expected format, visual effects are applied automatically.

Controls and behaviors:
- The visualiser initialises automatically when the Now Playing view loads.
- It will attempt to reconnect when the WebSocket connection is lost (configurable).
- The visualiser can be programmatically controlled via the exported helpers in the plugin code (see `src/index.ts`).

## Settings

This plugin exposes an example settings UI. You can view or extend the settings in [src/Settings.tsx](src/Settings.tsx).

### Dev options
The internal visualiser supports the following options (defaults shown):

- `wsUrl` (string) — default `ws://localhost:5343`
- `autoReconnect` (boolean) — default `true`
- `maxReconnectAttempts` (number) — default `5` (visualiser tries progressively)
- `reconnectDelay` (number ms) — default `2000`
- `lerpFactor` (number 0..1) — default `0.5` (smoothness of transitions)
- `showStats` (boolean) — default `false`
- `showStatus` (boolean) — default `false`
- `zIndex` (number) — default `-3`

To change options, edit the code that instantiates the visualiser in [src/index.ts](src/index.ts).

The audio analysis server used by Reactivo is maintained in a separate project: https://github.com/itzzexcel/reactivo. If you want the analysis server source, example agents, or platform builds, check that repo.

## Development

Files to note:
- [src/index.ts](src/index.ts) — plugin lifecycle and connection logic
- [src/giragira.ts](src/giragira.ts) — visualiser core, DOM/CSS logic and WS handling
- [src/Settings.tsx](src/Settings.tsx) — plugin settings UI
- [src/ui-interface.ts](src/ui-interface.ts) — helper to locate the Now Playing container

If you develop locally in this monorepo, use the top-level watch/build scripts (see repository README) to rebuild the plugin and test changes in the Luna app.

### Bundled helper (Windows)
This plugin includes a small helper executable bundled for Windows at `src/net9.0-windows.zip`. You can unzip and run the contained installer to install a local analysis helper — the binary is provided to simplify testing on Windows, but the full analyser source and other platform builds are in the external repo above.

## Troubleshooting

- Visual effects not appearing: check the browser devtools console for errors and ensure the plugin can find the Now Playing container (look at `GetNPView` in `src/ui-interface.ts`).
- No connection / disconnected status: verify a compatible WebSocket audio analysis server is running and listening at `ws://localhost:5343` (or change `wsUrl`).