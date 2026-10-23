# Big Steps

Big Walk, but you walk with Baby Steps' legs.

a BepInEx plugin for Big Walk that replaces the bird's walking with a port of Baby Steps' leg physics: each leg is lifted, steered and put down on its own, your weight follows the planted foot, and if you lean too far or stretch too wide you fall over, ragdoll and get back up. the bird, its arms and the rest of Big Walk stay as they are.

## what's in it

- **legs:** Nate's walking from Baby Steps, sized to the bird's legs (height adjustable), with Baby Steps' slope grip and surfaces mapped onto Big Walk's terrain.
- **falls:** floppy ragdoll, get-up timing like Baby Steps. a long fall drops what you hold, like vanilla.
- **camera:** optional third person with Baby Steps' framing and shoulder switch; the crosshair sits on whatever you'd grab or press. binoculars go first person while you look through them.
- **moving stuff:** trains, lifts and other moving platforms carry you (and your ragdoll). seats, the ski lift and train handholds hand control back to Big Walk while you're on them.
- **multiplayer:** modded players see each other's legs and falls. vanilla players can play in the same lobby and see modded birds walk the normal Big Walk way. a modded host can share its walking settings with the lobby.
- **options:** a Big Steps category in Big Walk's own settings menu (defaults, height, grip, camera, keyboard and gamepad rebinding), and a controls card beside Big Walk's in the pause menu.

## install

1. install [BepInEx 6 IL2CPP](https://builds.bepinex.dev/projects/bepinex_be) (tested with `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785`) into the Big Walk folder and start the game once.
2. drop `BigSteps.dll` in `Big Walk/BepInEx/plugins/`.
3. linux / steam deck: set the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`.

press **F9** in game to switch the legs on (or set the default walking state in Settings > Big Steps). the options are in the pause menu under Settings > Big Steps.

## controls (legs on)

| | gamepad | keyboard / mouse |
|---|---|---|
| lift left / right leg | LT / RT | left / right mouse |
| place the lifted foot | left stick | WASD (left shift = careful) |
| crouch | both triggers | |
| raise arms | X / B | Q / E |
| reach / use / drop | bumpers | F / G |
| switch camera side | A | Space |
| legs on / off, third person | | F9, F6 |

everything is rebindable in Settings > Big Steps > Controls. jump and crouch are off while the legs are on (Big Walk's controls come back while you sit or hang on to something).

## build

.NET 6 SDK and a Big Walk install with BepInEx run once (it generates `BepInEx/interop`, which the project references):

```
dotnet build src/BigSteps.csproj -c Release -p:GameDir="<your Big Walk folder>"
```

## source

- `src/Sim/` is the walking itself, engine-free (no UnityEngine): a reimplementation of how Nate walks, its tuning values and curves measured from Baby Steps.
- the rest of `src/` fits it onto the bird and Big Walk (body, ragdoll, camera, arms, platforms, menus, multiplayer).

no game files, assets or decompiled code are included in this repo.

## credits

- [Baby Steps](https://store.steampowered.com/app/1281040/) by Gabe Cuzzillo, Maxi Boch and Bennett Foddy. the walking is a port of how Nate walks.
- [Big Walk](https://store.steampowered.com/app/1478500/) by House House.
- the multiplayer side channel (Steam P2P calls, snapshot interpolation) follows the approach of the CC0 [Baby Steps Multiplayer mod](https://github.com/caleborchard/Baby-Steps-Multiplayer-Mod-Client) by Caleb Orchard.

unofficial fan mod, not affiliated with or endorsed by House House, Devolver Digital or the Baby Steps team.

## tested on

Big Walk 1.6.0, BepInEx 6.0.0-be.785, Steam Deck / Proton 11, with and without other players (modded and vanilla).
