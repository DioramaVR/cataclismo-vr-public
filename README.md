# Cataclismo VR

A VR mod for Cataclismo: your fortress in front of you, like a model you
can move, scale and turn. Point the laser at the map and the game's
menus to build, select and give orders, as you would with the mouse.

Part of [Diorama VR](https://dioramavr.app). Install it with the Diorama VR
installer; it sets everything up and puts the game back to normal when you
uninstall. You need a Windows PC, a SteamVR headset and the game on Steam.

## This repository

Each release's source, published by the release workflow, and the files the
installer and the site read:

- `data/index.json`: every release (version, maturity, tested game builds,
  download, SHA-256) and the known issues.
- `data/game.json`: the game's description, controls and tutorial.
- `src/`: the plugin (BepInEx 6, IL2CPP). It builds against the Diorama VR
  Unity core at the version pinned in `Directory.Build.props`
  (`DvrCoreVersion`) and needs your own copy of the game's DLLs; the release
  zips are the supported way to install it.
- `lib/`: OpenVR's `openvr_api.dll` and the mod's shader bundle
  (`catavr-shaders`).

Not affiliated with Digital Sun or Hooded Horse. Cataclismo is theirs.

## Licence

MIT, see `LICENSE`.
