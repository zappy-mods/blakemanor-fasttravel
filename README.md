# Blake Manor Fast Travel

A [BepInEx](https://github.com/BepInEx/BepInEx) mod for **The Seance of Blake Manor** that adds a fast-travel menu for jumping directly to any room you've already visited, instead of walking there.

## Features

- **Fast travel menu** — press the hotkey (`F9` by default) to open a list of every room you've discovered, and click one to travel there instantly.
- **Respects the story's own locks** — a room you haven't earned access to yet (a locked door you don't have the key for, or one only open at certain times, like the Dining Room during meal hours) won't show up until you actually could reach it the normal way. This is detected live from the game's own door logic as you explore, not hardcoded.
- **"Bypass Locks" toggle** — a button in the menu lets you ignore those checks if you'd rather have unrestricted fast travel. Off by default, resets every session.
- **Resizable, draggable window** with text that scales with it.
- **Rebindable hotkey** and an optional diagnostics mode, both via the BepInEx config file.

## Requirements

- [BepInEx 5.4.x](https://github.com/BepInEx/BepInEx/releases) (Mono, not IL2CPP) installed for The Seance of Blake Manor.

## Installation

1. Install BepInEx 5.4.x into your game folder if you haven't already (run the game once afterward so BepInEx finishes setting itself up).
2. Download the latest release zip from the [Releases](../../releases) page.
3. Extract it into your game's install folder, so you end up with:
   ```
   The Seance of Blake Manor/
     BepInEx/
       plugins/
         BlakeManorFastTravel/
           BlakeManorFastTravel.dll
   ```
4. Launch the game.

## Usage

Press **F9** in-game to open the fast-travel menu. Click a destination to travel there, or **Close**/**Esc** to dismiss the menu. The **Bypass Locks** button in the bottom-left toggles whether key/time-of-day gates are enforced for that session.

### Configuration

After running the game once with the mod installed, a config file appears at `BepInEx/config/zappymods.blakemanor.fasttravel.cfg`:

- **General.Hotkey** — the key that opens/closes the menu (default `F9`). Accepts any [Unity InputSystem Key](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.7/api/UnityEngine.InputSystem.Key.html) name.
- **Diagnostics.EnableDiagnosticLogging** — off by default. Turn this on only if asked to when reporting a bug; it adds extra logging (and a `door_links.log` file next to the DLL) to help troubleshoot issues.

## Known limitations

- Room availability (which locked/time-gated rooms show up) is learned by observing doors as you explore — a room you've never been near yet may not have its lock condition detected until you walk past its door at least once, even with **Bypass Locks** off. This only ever makes fast travel *more* restrictive than intended, never less; it doesn't affect basic destination discovery (which room.<handle> is set already covers travel to any room you've physically visited).

## Building from source

Requires the .NET SDK. This project references the game's own assemblies to build against, so point it at your install:

```bash
# either set an environment variable once...
export BLAKEMANOR_GAME_DIR="/path/to/The Seance of Blake Manor"
dotnet build src/BlakeManorFastTravel

# ...or pass it on the command line each time
dotnet build src/BlakeManorFastTravel -p:GameDir="/path/to/The Seance of Blake Manor"
```

## License

[MIT](LICENSE)
