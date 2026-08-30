# Changelog

All notable changes to this project are documented here. Format loosely follows [Keep a Changelog](https://keepachangelog.com/).

## [0.0.1] - 2026-08-30

Initial release.

### Added
- Fast-travel menu (default hotkey `F9`) listing every room discovered so far.
- Live key/time-of-day gating: destinations you haven't actually earned access to yet (a locked door without its key, a room only open at certain times) are excluded automatically, detected from the game's own door logic rather than a hardcoded list.
- "Bypass Locks" toggle in the menu to ignore those gates for the session.
- Resizable, draggable menu window with text/UI that scales with it.
- Rebindable hotkey and an opt-in diagnostics logging mode, both configurable via the BepInEx config file.

### Fixed
- Several base-game issues that fast travel's ability to jump directly between any two rooms exposed but normal door-by-door movement mostly avoids, including a crash from loading an out-of-date scene variant, a couple of severe log-spam performance hits that could make a scene load look frozen for a long time, and a `Time.timeScale` race that could leave a room's on-enter cutscene (and the player) stuck indefinitely.
