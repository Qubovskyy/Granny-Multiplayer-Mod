# Granny MP – multiplayer mod (BepInEx 6 IL2CPP)

Mod version: **0.9.0 (beta)** (protocol 36) · Game: Granny (Steam, Unity 6000.0.58, IL2CPP) · BepInEx 6.0.0-be.785

Online co-op and PvP for Granny, up to 8 players (9 via config). The host is authoritative: only the host runs the AI
(Granny, spiders, crow, rats), the item placement and the world logic; clients send their movement and actions, the host
validates them and tells everybody. English by default, Polish in the settings. Discord: https://discord.gg/MrKK6KwMVv

---

## Installation

Every player needs the same Steam version of Granny, **BepInEx 6 IL2CPP (be.785) [(Download BepInEx here)](https://builds.bepinex.dev/projects/bepinex_be/785/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785%2B6abdba4.zip)** and `GrannyMP.dll` in
`BepInEx\plugins\GrannyMP\` – it is a single file (the mod's pictures are built in). Character models (`.gmdl`) go to
`BepInEx\plugins\GrannyMP\models\`. The first start creates `BepInEx\config\pl.grannymp.coop.cfg`.

## How to play

Press **F1** in the main menu → **Host Game** or **Join Game**. Your name and everything else is in the settings (gear).

* **Host** – via **Steam** (recommended: friends only or public, no port forwarding; *Invite friends*, *Copy Lobby ID*) or
  **Direct IP** (UDP, default port 27777 – port forwarding or a VPN like Radmin / ZeroTier over the internet). Optional password.
* **Join** – the list of Steam games (public + friends), **Direct...** for a Lobby ID / host SteamID / IP:port, or a Steam invite.
  A game with a password asks for it first.
* **Start** – clients click **READY**, the host picks the difficulty and options in the game's menu (they are shown on the clients'
  menus) and clicks **START GAME** in F1. Everybody loads the house and starts at the same moment; only the host can skip the intro.
* **Esc** is the normal pause menu (it does not stop the game in multiplayer); *Main menu* leaves the session.

| Key | Action |
|---|---|
| F1 | lobby window (in game too); the gear opens the settings |
| T | chat |
| V | voice chat (push to talk / toggle / always on) |
| LMB / RMB / B | PvP Granny: grab, say a line / laugh, bear trap (rebindable) |

**Settings:** name, language, interface size (75–300 %, auto), field of view, resolution and window mode (*Apply*), skip the
intro, name tags, advanced logs, your character (preview window), key bindings, voice chat (device, volumes, activation level,
microphone test).

**Server configuration** (host; locked during a game): joining during the game, one player per hiding spot, Granny catches
everybody in the spot, FOV lock, voice chat range, and in PvP Granny's speed and who plays Granny.

## Modes

* **Shared days** – anybody caught = everybody loses a day (like the original).
* **Last survivor** – a caught player spectates the living through their eyes (LMB / RMB = next / previous player) until the
  end of the day; the day is lost when everybody is caught.
* **PvP** – one player (random or chosen) plays Granny, the others are survivors; survivors win by escaping, Granny when the days
  run out. The Granny player:
  * walks with the normal controls at Granny's chase speed for the difficulty (× `GrannySpeedFactor`), sees through her eyes and
    in the dark, opens doors (also the backyard door only her AI could pass) but no drawers, cupboards, levers, items or hiding spots;
  * grabs a survivor in front of her (also in a hiding spot next to her), says her lines, puts bear traps down (every 30 s);
  * sees a survivor who makes a noise through the walls for 4 s (a red ring with the distance);
  * is knocked out by her own code (shotgun, crossbow, pepper, car, freeze trap) – a black screen until she gets up;
  * waits at the start of every day (black screen, then a 30 s countdown – a head start for the survivors);
  * cannot fall where Granny's AI never goes (holes, the water outside). PvP cannot be started in *Practice*.

Granny targets the nearest player she can see (or the one with the teddy, or the one she heard); hiding works like in single
player. When anybody escapes, everybody goes to the ending.

**Characters:** the other players are shown as the character they chose (man, woman, 63 bundled low-poly characters or custom
`.gmdl` files), animated from their movement. Make your own from a Mixamo-rigged FBX with the **Model Tool**
(`Tools\GrannyModelTool.exe`, .NET 8 Desktop Runtime; built-in Idle / Walking / Crouching animations); every player who should
see it needs the same file.

## What is synchronised

* **Players** – position, look, crouching, hiding, held item, flashlight light, what each player sees (for spectators), voice
  (positional), chat, name tags with DEV / Helper badges.
* **AI** – Granny (pose, animations, voice lines, eyes, sight / chase / catch against every player), both spiders, the crow, rats,
  Granny's mother; hits on Granny from any player (shotgun, darts, pepper spray, car, freeze trap); her notices ("gone for about
  1 minute", extra lock, extra day).
* **World** – doors, drawers and everything animated, script state (car parts, locks, the escape progress), items picked up /
  dropped / thrown (with loaded weapons and their size), physics props (simulated by the host or by the player who pushes them),
  traps, random item placement, sauna, fuel, lamps, painting pieces, the bumper, the garage gate.
* **Sounds** – one-shot sounds (with the exact clip), loops and sound holders, AI music (chase music only for the chased player),
  noise triggers forwarded to the host so Granny hears everybody.
* **Game flow** – the intro, the start of the game, days, catches, jumpscares, game over, escapes and their cutscenes; the host's
  settings (restored after the session).

Safety: protocol version, password, player limit, distance and speed validation of what clients change, sound rate limit;
clients leave when the host does (also over Steam).

## Configuration (`BepInEx\config\pl.grannymp.coop.cfg`)

Everything common is in the F1 settings. Also: `[Lobby] MaxPlayers`, `DirectIPPort`, `Password`, `SteamLobbyType`, `DefaultMode`;
`[PvP] GrannySpeedFactor`, `GrannyCameraHeight`, `GrannyNightVision`, `GrabKey`, `VoiceKey`, `TrapKey`; `[Network] TickRate`,
`DisableSteam`; `[Sync] MovementValidation`, `MaxSpeed`, `ReplicateSounds`; `[Advanced]` – lists of host-only scripts, forwarded
triggers and synced fields (tuning without recompiling); `[Debug] VerboseLogging` – detailed `LogOutput.log` for bug reports.
F9 (DEV SteamIDs only, host): spawn every item, god mode.

## Two copies on one PC (testing)

Set `[Network] DisableSteam = true`, start `Granny.exe` twice from the game folder (e.g. `-screen-fullscreen 0 -screen-width 1280
-screen-height 720`), host Direct IP in one and join `127.0.0.1:27777` in the other. Set it back to `false` for Steam.

## Bugs

Enable *Advanced logs*, play until the problem shows and send `BepInEx\LogOutput.log` of every player (host and clients).

## Building from source

Windows, .NET SDK 6+, Granny with BepInEx 6.0.0-be.785 IL2CPP started once (it generates `BepInEx\interop`, which the project
compiles against – not in this repository).

1. Copy `GrannyMP.local.props.example` to `GrannyMP.local.props` and set `GameDir` (or `-p:GameDir="C:\...\Granny"`).
2. `build.bat` (or `dotnet build -c Release`) – `GrannyMP.dll` goes straight to `<GameDir>\BepInEx\plugins\GrannyMP\`;
   `assets\*.png` are embedded into it.
3. Model Tool: `tools\ModelTool`, `dotnet publish -c Release` (its built-in animations come from `tools\ModelTool\DefaultAnims\*.fbx`,
   not in the repository). Character models are third-party art and not in the repository either.

```
src/Core   Plugin, Cfg, Loc (EN/PL), NetRunner (loop), Patches (Harmony), Png, Util, Display, DebugMenu
src/Net    Protocol, Transport, SteamNative, SteamTransport, UdpTransport, Session (lobby), LobbyBrowser
src/Sync   World (objects), PlayerSync, AiControl, GameFlow, Events (triggers, sounds), Pvp, Spectator, VoiceChat, … (one file per feature)
src/UI     LobbyUI (lobby, settings, HUD, chat), MenuBrand (main menu picture)
```
