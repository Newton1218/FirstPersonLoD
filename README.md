# FirstPersonLoD

A first-person VR mod for **Legend of Dungeon** (RobotLovesKitty, 2013). You play the dungeon from
inside it: your hands are the controllers, swords swing with your arm, guns aim where you point
them, and the rooms are rebuilt around you with real depth, walls, ceilings and HD stone.
Pressing a view switch gives you back the game's own tabletop view, untouched, at any time.

It is a BepInEx 5 plugin on top of your own copy of the game. Nothing of the game is included.

## What you need

- Legend of Dungeon on Steam, **Windows**, switched to the game's **VR beta branch**
  (Steam > Legend of Dungeon > Properties > Betas). The mod is built for that branch
  (Unity 5.6, 32-bit, SteamVR) and does nothing on the regular branch.
- SteamVR and a headset. Controller layouts are included for the **Steam Frame**, **Quest / Rift
  (Touch)** and **Index**. The Frame layout is the one played the most so far.
- A PC that holds 90 fps in VR (the mod watches its own frame time and logs it).

## Install

1. Download the latest zip from this repository's **Releases** page (right side of the page) and
   unzip it anywhere.
2. Double-click `install.bat`. It finds the game in your Steam libraries, downloads BepInEx 5
   (32- or 64-bit to match the game) if it is not there yet, and copies the plugin in.
3. Start the game from Steam with SteamVR running. The installer then asks you to quit the game
   and checks the log to confirm the plugin loaded.

Updating: unzip the new release and run `install.bat` again.

## Controls

The mod rewrites the game's SteamVR controller files (keeping the originals as `*.orig`) so these
work out of the box. You can still change anything in SteamVR's controller bindings.

| | Steam Frame | Quest / Rift (Touch) |
|---|---|---|
| Move / turn | left stick / right stick (snap turn) | same |
| Attack, shoot, use | right trigger (or swing the weapon) | same |
| Jump | A | A |
| Next / previous item | D-pad right / left | B / left trigger |
| Hotkeys | D-pad up / down | Y / X |
| Drop item | left grip | left grip |
| Free hand: hit a switch | left trigger with the left hand on it (or pointing at it) | same (left trigger is "previous item" when nothing is in reach) |
| Inventory screen | hold left bumper | hold right stick click |
| Bare hands (again = back) | hold right bumper | hold right grip |
| Pause | right Menu button | hold left stick click (half a second, stick centred) |
| Recenter | hold left View | pause menu |
| Tabletop view / first person | hold A + left trigger for 2 s | same |
| Every item + full ammo | hold B + left trigger for 2 s | same |

Two-handed guns: put your left hand on a gun's barrel and it is held in both hands. Coins and items
within about an arm's length slide to you and are picked up.

Keyboard (game window focused): F6 first person on/off, F7 save the current tuning, F11 input
diagnostics.

## Settings

All settings live in `BepInEx\config\FirstPersonLoD.cfg` (created on first run; deleting it
restores the defaults). A few worth knowing:

- `FPRenderScale` supersampling in first person (0 = SteamVR decides; 1.2 to 1.3 smooths edges)
- `BrickRough`, `StoneHDStrength` how rough and how detailed the stone is
- `StatueDepth` thickness of the carved angels
- `RoomCeilings`, `Crates3D`, `DoorTunnels` the rebuilt rooms, crates and doorways
- `PickupMagnet`, `PickupRadius` the item magnet
- `TwoHandGuns`, `OffHandUse` the free hand
- `GiveAllCombo` the every-item combo (set to false to turn it off)

## When something goes wrong

The mod writes a detailed log: `LegendofDungeon_Data\output_log.txt` in the game folder
(or `BepInEx\LogOutput.log`). Send that file along with what happened; nearly every feature
logs what it saw and decided.

## Uninstall

Double-click `uninstall.bat`: it removes the plugin and restores the game's own controller files.
BepInEx stays (delete `winhttp.dll`, `doorstop_config.ini` and the `BepInEx` folder to remove it).

## Credits

Legend of Dungeon is by RobotLovesKitty. This is an unofficial fan mod, not affiliated with them.
Uses BepInEx (https://github.com/BepInEx/BepInEx).

## Building from source

Players only need the release zip. To build the plugin yourself:

- The code is one file, `src/FirstPersonLoD.cs`, compiled as a .NET 3.5 class library with the
  `BETA` symbol defined, against the game's own assemblies (`LegendofDungeon_Data\Managed`:
  `UnityEngine.dll`, `Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll`) and BepInEx 5's
  `BepInEx.dll`. None of those are in this repository: they belong to the game's developer and to
  BepInEx, so build against your own copy.
- With Mono's compiler, for example:
  `mcs -target:library -define:BETA -out:FirstPersonLoD.dll -r:BepInEx.dll -r:UnityEngine.dll -r:Assembly-CSharp.dll -r:Assembly-CSharp-firstpass.dll src/FirstPersonLoD.cs`
- If your compiler's mscorlib is not the Unity 5.6 one, `tools/patch_corlib.py` retargets the
  finished DLL's mscorlib reference to the version the game loads (needs Python and `dnfile`).
