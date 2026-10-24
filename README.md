# Granny MP – multiplayer mod (BepInEx 6 IL2CPP)

Mod version: **1.23.1** (protocol 29) · Game: Granny (Steam, Unity 6000.0.58, IL2CPP) · BepInEx 6.0.0-be.785

Online co-op for Granny for up to 8 players (host + 7; up to 9 via config).
The host is authoritative: only the host runs the AI (Granny, spider, crow, rats), item randomisation and world logic.
Clients send their movement and the changes they make near themselves; the host validates and rebroadcasts them.

The mod is in **English** by default. Polish is available: the UK / Polish flag buttons in the F1 window or `[General] Language` in the config.

---

## Installation

Every player needs:

1. The same Steam version of Granny.
2. BepInEx 6 IL2CPP (be.785) – exactly as in this folder.
3. `GrannyMP.dll` in `BepInEx\plugins\GrannyMP\`.

The easiest way is to give friends the `BepInEx` and `dotnet` folders plus `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`.

The first start creates `BepInEx\config\pl.grannymp.coop.cfg`.

---

## How to play

Press **F1** in the menu – the lobby window opens.

### Hosting
First choose the method: **Steam** or **Direct IP** – only the options of that method are shown.
* **Host via Steam (recommended)** – creates a Steam lobby, **Friends only** or **Public**. Steam handles NAT/relay, no port forwarding needed.
  * **Invite friends** opens the Steam overlay.
  * **Copy Lobby ID** / **Copy host SteamID** – for a friend to paste.
* **Host Direct IP** – UDP server on the given port (default 27777). Over the internet you need UDP port forwarding
  or a VPN (Radmin VPN, ZeroTier, Hamachi). **Copy IP:port** copies the LAN address.
* Optional **password** (field next to the host buttons). Players joining through a Steam invite are asked for it.

### Joining
* **Steam games list** – *Refresh* shows public games of this mod (same version) and games of your Steam friends (★). Click *Join*.
* Or paste a **Lobby ID**, **host SteamID** or **IP:port** and click **Join**.
* Or accept a Steam invite (works even when the game is closed – Steam starts it with `+connect_lobby`).

### Starting the game
Everybody starts at the same moment: after loading Granny's house every game waits (black screen, "Waiting until everybody has
loaded…") until the host sees all players ready, then all start together (at most 30 s; a player joining during the game starts at once).

1. Clients click **READY** (only in the lobby – once the game runs there is no ready / not ready).
2. The host chooses difficulty and options in the game menu (Play / Options: Nightmare, extra padlocks, intro…). While everybody is in the menu, the host's **Game theme, Difficulty, Extra locks, Light sources and Skip intro** are shown on the clients' menus (also right after a client joins) – on a client these controls are switched off
   (greyed out, not clickable). The main menu shows *Multiplayer* under the GRANNY logo; the mod's own window is called **GRANNY MP**.
3. The host clicks **START GAME** in the F1 window. Only the game's *Continue* button is hidden while in a lobby – Play and its options stay.
4. If the intro is enabled, everybody watches it. **Only the host can skip it** – the skip is synchronised.
5. Everybody loads Granny's house. The host's settings (PlayerPrefs and difficulty) are sent to the clients;
   your own settings are restored after you leave the session.

### Pause menu
* **Esc** opens the normal pause menu. *Resume* works as usual.
* *Main menu* leaves the session (disconnects) and returns to the menu.
* The pause menu does **not** stop the game in multiplayer (for the host too) – Granny and the world keep running.

### Controls
| Key | Action |
|---|---|
| F1 | lobby window (also in game: leave to menu); the **gear** button opens the settings |
| T | in-game chat (Enter – send, Esc – cancel) |
| V | proximity voice chat: hold (push to talk) or press (toggle), or no key at all (always on) |

### Settings (gear in the F1 window)
* Language (flags), interface size (Auto = 100% at 1080p, 200% at 4K), **field of view** (the game's own, or 60–120°; the host
  can lock it), **resolution** and **window mode** (fullscreen / borderless window / window – choose with < > and click *Apply*;
  kept in `[Display]` and applied again at every game start), skip the DVloper intro at game start, name tags, advanced logs (for bug reports).
* **Your character** (under *Advanced logs*) – *Change* opens a window with turning previews of all characters (man, woman, the 63 bundled
  low-poly characters *Character 01 … 33*, *Character Female 02 … 16* incl. police / firefighter / doctor versions, and custom ones),
  4 in a row and 2 rows at a time – more characters: the mouse wheel or the arrows on the right; a click on a character chooses it
  (that is what the other players see).
* Key bindings: lobby window, chat, voice (click, press the new key; middle / side mouse buttons work; Esc cancels).
* Voice chat: on / off, **push to talk / toggle / always** (voice activation with an adjustable level), microphone device,
  microphone volume, voice chat volume, **microphone test** (you hear yourself and see the level bar).
* While you talk (or the microphone is switched on in toggle mode) a microphone icon in the bottom-right corner shows how loud you are.

### Modes (chosen by the host)
**Co-op** or **PvP**, and for both the day rules (all of it only in the lobby, before the game starts):
* **Shared days** – anybody caught = everybody loses a day (like the original).
* **Last survivor** – a caught player first sees the catch exactly like in single player (Granny's grab and the jumpscare;
  everybody else sees Granny stop and grab), then **spectates** the others until the end of the day: they look through the eyes of a living
  player (left / right mouse button = next / previous player), are invisible, ignored by Granny and cannot touch anything or hide.
  The spectator sees exactly the watched player's screen: that player's own camera position, rotation and field of view are sent
  (head bob, crouching, hiding-spot cameras, the catch animation) and replayed smoothly a moment later, through a copy of the
  player's own camera (same post-processing). The item they were holding is dropped (also one the game drops during the catch –
  the others see it), their own flashlight / lantern and hand items are switched off, and so are the copies of their flashlight / lantern
  light in the game's catch scenes (Granny's jumpscare camera, the spider's mouth), which used to stay lit where they died. The spectator sees the watched player's flashlight / lantern
  and held item in their hand, with the real light (shadows, flashlight pattern), the crosshair and the watched player's chase music.
  When the watched player hides (bed, coffin, car) the spectator looks through the same hiding-spot camera. A dead player has no name tag
  and the "hidden" picture in the corner is switched off while spectating.
  Esc opens the pause menu as usual. The day is lost only when everybody is caught. The next day revives everybody.
  While spectating and during the change of day the game's fall timer stays at 0 (it used to add up and the first step out of bed
  the next day counted as a deadly fall), and Granny neither hunts nor catches anybody while the day changes.
* **PvP (player Granny)** – when the game starts a player **plays Granny** (a random one, or the one the host chose in the server
  configuration); the others are survivors and play by the chosen day rules (*Shared days*: Granny catching anybody ends the day for
  everybody; *Last survivor*: a caught survivor spectates the other survivors, the day is lost when all survivors are caught). The
  survivors win by escaping, Granny wins when the days run out. With one player only, or when the Granny player leaves, the computer
  plays Granny again.
  * The Granny player walks around the house with the normal controls (doors yes, also the small ones like the cellar door under
    the stairs – drawers, cupboards, wardrobes, the fridge, levers and the car no; no crouching); everybody else sees
    Granny herself walking there (her walk / stand animations), not a player model and no name tag. The Granny player sees Granny's
    own body when looking down (her head is folded away so it does not block the view, `[PvP] GrannySeesOwnBody`), the camera is a
    little below Granny's eyes (`[PvP] GrannyCameraHeight`, default 0.9 of their height), and Granny sees in the dark (a light around her eyes on the Granny player's screen only,
    `[PvP] GrannyNightVision`). Granny moves like in single player: at her chase speed for the chosen difficulty
    (easy 2.5, normal 3.8, hard 5, extreme 8.5 m/s; the host sends it), times `[PvP] GrannySpeedFactor` (default 1).
  * **LMB** – grab a survivor in front of you (about 2 m) – also one hiding in a bed / coffin / car / wardrobe right next to you
    (under a bed Granny looks under it like in single player). The catch is the normal one: Granny's grab, the jumpscare for the victim.
    A miss is a swing at the air (a short pause). **RMB** – Granny says one of her lines or laughs, **B** – she stops and puts a bear trap
    down at her feet at once (her own trap code, which in single player waits 10 s first; every 30 s). The keys are in `[PvP]`. The bar at the bottom of the screen shows them;
    top right a bear-trap icon (`beartrap-icon.png` next to the mod) with its key shows when the next trap is ready. While Granny swings, puts a trap down, catches or is
    knocked out the Granny player cannot walk.
  * Shot (shotgun, crossbow), sprayed with pepper, hit by the car, frozen: Granny is knocked out by her own code and the Granny player
    cannot move until she gets up.
  * The Granny player cannot pick up or use items, has no flashlight or lantern (the light-sources option; the ones on the tables are not
    shown to her), cannot hide (the hide icon is not shown), spring traps or make noise for the sensors, and sees no name tags.
  * At the start of every day the Granny player's screen is black with a **30 s countdown** – a head start for the survivors. Meanwhile
    the game wakes them up and they are put where Granny starts the day (one of her four random start positions: the upper floor,
    the ground floor by the TV room, the basement, the yard side); when the countdown ends they can move.

**Granny's target:** every 0.25 s Granny checks all players. A player she can **see** (line of sight from her eye) beats one she cannot;
among visible players the **nearest** wins – also during a chase, when another visible player is clearly closer or the chased one has been out of sight for 2 s.
Hiding works like in single player: if a player hides **while Granny is chasing them** (or saw them less than 2 s before), she walks to
the hiding place, turns to it, waits 3 s and pulls them out (under a bed like in single player: she looks under the bed with her head-follow off,
the bed camera turns to her face and that bed's scream plays for the victim – the others hear it from the bed; the host's own bed catch
goes through the multiplayer end of day, so in *Last survivor* the host becomes a spectator instead of starting a new day).
A player who hides unseen is safe; when another player is visible she goes for that one instead. Spectators are never targets.
A player holding the **teddy bear** is always her target (like in single player she knows where it is).
When nobody is visible she investigates the player she last heard, otherwise the nearest one.

**Server configuration** (F1 → *Server configuration...*; the host sets it, the others see it): joining during the game, the hiding-spot
options below, the field-of-view lock (everybody plays with the game's own FOV) and, for PvP, who plays Granny (a random player or a
chosen one).

**Hiding spots (server configuration):**
* **One player per hiding spot** – nobody can get into a bed / coffin / car somebody else already uses ("Somebody is already hiding there").
* **Granny catches everybody in the hiding spot** – when she finds a player in a hiding spot, the others hiding in the same spot are caught too;
  switched off, only the player she found is caught.

**Stunned Granny** (crossbow, shotgun, car, pepper spray, freeze trap) cannot catch anybody until she gets up.

**Player models:** the other players are shown as the character they chose in the settings (man, woman or a custom one), animated from the synced movement:
standing, walking (faster when running), crouching, walking crouched, lying in a hiding spot. The model stands on the floor (the lowest
point of the animated model on the floor found under the player) and it is a little taller than that player's camera is high
(`[General] PlayerModelSize`, default 1.15). The fingers keep
the model's own straight pose (Mixamo animations curl a full hand; the bundled models have one mitten per hand). The head follows where the player looks (up / down, left / right) and the
body turns after it (at once while walking, standing still only when the head is turned too far); lying under a bed the head is raised.
The material is matte (smoothness 0, no specular highlights or reflections). The models are in `BepInEx\plugins\GrannyMP\models\`
(`male.gmdl`, `female.gmdl` and the `Character_*.gmdl` pack; the game cannot import FBX at runtime). Without those files the simple capsule avatar is used.
**Custom characters:** any `.gmdl` file in that folder appears in the character window (the file name is its name). Make one from an FBX with a
Mixamo skeleton with the **Model Tool** (`Tools\GrannyModelTool.exe`, source in `tools\ModelTool` – no Unity needed):
choose (or drop onto the window) the character FBX, its texture (PNG / JPG / TGA; empty = the one packed in the FBX or a PNG with the same name)
and optionally the animations – **every animation is optional**: an empty field uses the tool's **built-in** Mixamo *Idle*, *Walking* and
*Crouching Idle* (made for the bundled characters' skeleton); your own Mixamo FBX files with the same skeleton can be chosen instead
(*Find animations next to the model* fills them from files next to the model, *Use the built-in animations* clears them; without
*Crouched Walking* the mod makes the crouched walk from walking + crouching), type the character's name and click
**Convert to .gmdl** – the file is saved straight into the mod's models folder. Command line: `GrannyModelTool model.fbx [--texture t.png]
[--idle a.fbx] [--walk b.fbx] [--crouch c.fbx] [--crouchwalk d.fbx] [--out name.gmdl] [--builtin-anims] [--no-builtin-anims]`
(animation files next to the model are used when found, the rest are built-in; `--builtin-anims` ignores the files next to the model);
`GrannyModelTool --info file.gmdl` prints a summary. The tool is in English.
The tool needs the .NET 8 (or newer) Desktop Runtime. The old Unity editor script `tools\ModelExport.cs` still works too.
Every player who should see the character needs the same file; a player without it sees the default man.

**Team badges:** the mod's developers have a yellow **[DEV]** and helpers a green **[Helper]** before their name in the lobby and the player list,
and a small DEV / Helper above their name tag (by SteamID).

**Escape:** when any player escapes, the whole team goes to the ending scene (in PvP the survivors win).

---

## What is synchronised

| Element | How |
|---|---|
| Player position, rotation, look up/down, crouch, hiding | client → host 20×/s, the host validates speed and broadcasts all states |
| Remote player avatars | capsule body + head, sized from the real camera height; name tags (not for spectators); the item held in hand is shown. A player hiding under a bed or in a coffin lies there (head at the hiding spot's camera), in the car the head is at the car's camera |
| What a player sees | the camera drawing the player's picture (position, rotation, field of view) is sent with the player state – the spectator view replays it |
| Granny and other AI | AI runs only on the host; clients get position (interpolated), animations and sounds. Granny never catches anybody through a wall, a closed door or a floor (a line from her eyes / chest to the player's head / chest must be free; players, items and triggers do not count) |
| The spider in the attic | its area trigger is forwarded from clients; the host remembers who is in the area and the spider walks to that player (not always to the host); when it reaches a client the host catches that player ("The spider caught …") and their own game plays the bite |
| The crow and the bird seed | a client filling the seed plate sets `CrowControl.CrowStartEat` in their own game – it goes to the host (`[Advanced] ClientAiFields`), the host's crow flies to the seed like in single player |
| Using items (battery into the car, spider key, cogwheels…) | how near a player is to an object is measured to its colliders, not its pivot (many house parts have the pivot at the house's origin – a hatch next to the player counted as far and was reverted); a fact the use sets in a script far away (`checkTheCar.batteryOK` on the GameController) goes to the host together with that action – only bools that became true |
| The spider in the spider cellar | position and animations go to everybody (`[Advanced] AiTypes` += `MomSpiderHead`); a client entering the cellar wakes it too (its start triggers are forwarded and also run on that client – scare sound, cellar music). It hunts the nearest player it sees (range and view angle of its eyes, no wall in between), not only the host; when it reaches a client, that player is caught ("The spider caught …") and their own game plays the spider's eat sequence. A spectating host is never hunted |
| PvP: the Granny player | the host's Granny has no AI: every frame the host puts her where the Granny player stands (on the navigation mesh, facing where they look) and plays her walk / stand animation by speed; the usual AI sync shows her to everybody. The Granny player's grab, voice and trap go to the host (`Pvp` message), which checks the distance on its own picture and runs the normal catch; the host tells the Granny player when Granny cannot move (knocked out, a catch). Whenever the Granny player is more than 3.5 m from Granny (start of the day, after a knock-out) they are put back at her |
| Granny seeing / chasing / catching clients | the host runs Granny's original sight and chase code against the remote player's position (her eye's target – the host camera – is placed at the remote player's head, so she also sees after the host died) |
| Levers and buttons with an effect far away (the painting lever moving the bookshelf 16 m away) | an animation far away that starts together with the player's action is sent with it, and the host accepts it with that action |
| The car's rear bumper | walking into it is forwarded to the host, which drops it; a part a script releases from an animated object is tracked from then on |
| Doors, drawers, fridge, safe, bars, coffins, garage, car… | animation clip replication (legacy `Animation` and `Animator`) + script state fields + the object's tag (doors and drawers keep "open / closed" in it); clients apply only real changes, never repeats of an older value. An animation stopped half way (the attic fan whose cable was cut) stops on every machine |
| Using items (car battery, cylinder head, spark plug, cutting pliers, cogwheels, winch handle, wheel crank, the book, the remote…) | what the game's `PickUp` switches on / off when an item is used (the part in the engine, the cut cable, the cogwheel in the lock…) is synced (`[Advanced] WorldFields`); world facts kept by scripts on the player (`PickUp` cogwheels, `fillingFuel` full tank, `playerVevar` winch handle, `UseRemote` lock) are synced field by field |
| Levers that act on Granny (sauna) and starting the car | the fields a client's own game sets on Granny (`[Advanced] ClientAiFields`) are sent to the host's Granny |
| Granny's mother (the book) | the book on the shelf makes her fall on every machine (her ragdoll is simulated by each player); when anybody comes close the host starts her crawl |
| Teddy in the cradle | the trigger runs on the host, Slendrina appears for everybody |
| Shotgun / crossbow | a dropped weapon keeps whether it is loaded – whoever picks it up gets it loaded or empty as it was. A second copy of a weapon a client has just dropped that appears in the host's game at the same place is removed (logged as "A second … appeared") |
| Flashlight / lantern | **not** synced – every player picks up their own (`[Advanced] PersonalItemTags`) |
| Shotgun parts on the table | `DeliverShotgunParts` – the placed parts and the finished shotgun are synced for everybody |
| Picking up items | the game destroys the picked object → the destroy is sent to everybody |
| Dropping / throwing items | the game creates a new clone → spawned for everybody, the dropper simulates the throw |
| Physics objects (chairs, boxes, keys, fallen boards…) | simulated by exactly one machine: the host, or the player who pushes / drops / throws the object (ownership + pose stream). On every other machine the body is kinematic and interpolated – no second simulation to fight with. Pushing hooks the game's own push code (`pushRigidbody`) |
| Traps placed by Granny, other runtime objects | prefab clone replication |
| Random item placement, escape progress | the groups the items lie in (`RandomPlace1..5`, which `startNewDay` activates at random) are synced by their active state, the host's choice wins; full world snapshot + state fields (`CheckExitDoor`, `haveObjects`…) |
| Intro | synchronised start; only the host can skip |
| Day counter, end of day, game over, jumpscare | the host decides when a day ends and broadcasts it with its day number; everybody runs the local game sequence. A client's own game never ends a day by itself (it asks the host), and the host sends its day number every 2 s between days |
| Granny's voice ("where are you?", laughing) | only the host's Granny talks; the line she picked is sent and played on her head on every machine (a new line replaces the previous one, like in single player). Every play of one of her lines on any AudioSource passes one gate: at most one line a second, each one logged with where it came from. A line from the network is played from her head (never with PlayClipAtPoint, whose default rolloff is heard across the whole house); with no Granny in the house it is not played at all |
| Teddy bear | a client holding it makes the host's Granny hunt that client |
| Voice chat | own voice chat (Windows audio, 16 kHz IMA ADPCM, ~8 KB/s per speaker) sent through the session – works with **Steam and Direct IP**; in Granny's house the voice comes from the speaker's direction and fades out with distance (`[Voice] Range`); in the lobby / menu it is not positional. Spectators hear the living around the player they watch, the living do not hear spectators |
| Noise (creaky floors, falling items, camera, sensors) | client triggers are forwarded to the host, so Granny hears everybody; the sound of an item the client threw is played back to that client too |
| Hits on Granny (pepper spray, tranquilliser, shotgun, car) | the call is forwarded to the host. The shotgun and the pepper spray do not call a method – they write Granny's `hitByGun` / `hitByPepper` / `hitByPepperStart` directly; on a client these fields go to the host (`[Advanced] ClientAiFields`), so a client's shot / spray knocks her out. A client's spray straight at Granny (≤ 3.5 m, in front, no wall) hits her even when the particles missed. Shooting the cellar spider (`MomSpiderHead.getShot`) and the crow in its cage (`CrowControl.shootInBur`) is sent the same way |
| Pepper spray cloud | whoever sprays: the others see the spray cloud on that player's spray, and so does a spectator watching them. Items in another player's hand are held where that player looks (turned up / down too) – the spray no longer points at the floor |
| Climbing out of the window | the window's own cameras carry their own flashlight / lantern light – a spectator watching that player gets the same light |
| Using items, levers, shots (interaction sync) | while the local player's own scripts run (PickUp, openDoors, UseRemote, shootGun, the crossbow, the spray), every object of the house they switch on / off or re-tag is sent – a client's to the host, which applies it and tells everybody (also players who join later). The game decides what is done by these objects: the wooden stick switches its target off (else "I need some kind of a lever"), the engine part the same, the book re-tags the shelf ("Maybe put something here"), the weapon key switches on the crossbow locker and all the tranquilizer darts (they appear only after that – not a Practice thing), the Switch Lock lever the lamps at the exit door, the sauna lever the steam. Far objects are included (they used to be reverted on the client). Only real things of the house are sent (with a model or a collider) – never a camera rig (the window jump), the on-screen weapon buttons or anything `[Advanced] ExcludePathRegex` names |
| Remote control | the door's lamp turns green (`UseRemote.lamp`) on every machine once the door is unlocked |
| Gas can | shooting it calls `explode.explodeNow` – run by the host (`[Advanced] HostRpcMethods`), which then runs it in every client's game too (the explosion of a dropped can is not world state); it hits Granny on the host. No notice for it |
| Freeze trap | Granny springs a trap only in the host's game (her copy on a client does not); every change of a trap (armed – green, snapped – red, steam, sound) is sent, so all copies are the same |
| Escapes | the main door leads to EndScene, which everybody loads (its cutscene plays there; the host's PlayerPrefs go with the change of scene, so everybody gets the same ending – with Slendrina when the teddy was given). The car through the garage tunnel and the spider cellar end with a cutscene inside the house – when it starts in one game, every other game plays the same cutscene |
| Thrown / dropped items | given back by the thrower's game a second after they left the hand, even while they still roll – anybody can pick them up |
| Characters up close | the closer the viewer, the darker their colour (the hand's flashlight / lantern washed them out to white) |
| Fuel | the fill bar of pouring the fuel is shared (the highest level counts) – another player goes on where the first stopped |
| Crow in the cage | the dart entering the cage (`crowArrowShot`) is decided by the host (forwarded trigger) – its "shot in the cage" no longer started again and again |
| Darts on Granny | the dart writes `EnemyAIGranny.hitByArrow` directly – sent to the host like the shotgun's hit |
| Car progress, the garage gate | facts the game keeps on the GameController (fuel, battery, cylinder head, spark plug, locks…) that became true in a client's game go to the host even without a click nearby (pouring the fuel); the garage gate's lock (`openDoors.garageportLock`) is synced |
| Meat for the attic spider | placing it sets `spiderControll.foodTime` – sent to the host (`[Advanced] ClientAiFields`), the spider goes to eat like in single player |
| Arrows / thrown items vs props | a fast body a client's game simulates takes over the prop in front of it (kept for 3 s even while it still lies still) together with what lies on / in it (the screwdriver in the can), so it is knocked over like in the host's game |
| Shotgun pick-up | the loaded state also switches the shoot button, the shooting ray and the shells, and sets the shotgun's pose in the hand (closed and loaded = the end of the "Load" clip, else "OpenEmpty"), as the game itself does at the pick-up |
| Garage gate | its animation (on Garage_EscapeTunnel) is synced; pouring the fuel, the well's winch and removing a bear trap count as interactions (what they switch off / destroy is sent) |
| Lamps at the main door | cutting the cables turns them green in every game (`CheckExitDoor.lampa1ok/lampa2ok`) |
| Car's rear bumper | once the host's game releases it, it is a loose body on the clients too – they can push it |
| Destroyed by using an item | an object the game destroys during an interaction (the car battery's spot when the battery goes in) is switched off for the others – no more "I need a car battery" there |
| Teddy bear | any player holding it counts for Granny's own teddy logic (her red eyes, the teddy music); she goes for that player; her red eyes (`EnemyAIGranny.grannyEyeColor`) are synced |
| Shotgun loaded state | read / set on the PickUp the shot checks (`shootGun.ammoCheckHolder`) – an emptied shotgun no longer arrives loaded and the other way round |
| Painting pieces | a piece put into the frame (`tavelbitTrigger.tb1Ram`–`tb4Ram`) shows on every machine |
| Sounds of clients' actions (doorbell, noise traps…) | executed on the host and sent back to everybody |
| Items lying on the floor | an item makes its "fell on the floor" noise (the sound and Granny hearing it) when it lands (moving downwards); while it lies there (moved less than 0.3 m, not falling) its trigger is ignored – Unity starts it again whenever the body is switched between kinematic and simulated, which happens when a thrown item is taken over by the host |
| The same sound twice | a clip the network has just played on an AudioSource is not played there again by the local game within 0.35 s, and a synced copy of a clip the local game has just played there is dropped |
| Chase music and other AI music | host's music state mirrored on clients (`[Advanced] MirroredMusic`); chase music is heard only by the chased player (and by spectators watching that player); the chase-music fader (`fadeUpDownGrannyHunt`) runs only on the host |
| Attic floor that breaks | the client that steps on it breaks it locally and simulates the falling boards itself (they switch their collider off on the floor like in single player, `[Advanced] OwnedLocalTriggerTypes`); the host breaks it for everybody |
| Alarms, loops, switched-on sound holders | play/stop state of every 3D AudioSource is synced (host ↔ clients near the source) |
| Sounds, chat, ready, ping, kick, password | session layer |

Safety: protocol version, password, player limit, distance validation of world changes, movement speed validation, sound rate limit.

---

## Configuration (`BepInEx\config\pl.grannymp.coop.cfg`)

* `[General] Language` (English / Polish), `PlayerName`, `LobbyKey`, `ChatKey`, `ShowNametags`, `SkipIntroSplash`, `InterfaceScale` (0 = auto).
* `[Lobby] MaxPlayers`, `DirectIPPort`, `Password`, `SteamLobbyType` (0 private / 1 friends / 2 public), `AllowJoinInProgress`, `DefaultMode`.
* `[PvP] GrannySpeedFactor` (times Granny's chase speed for the difficulty), `GrannyNightVision`, `TrapKey` (B).
* `[General] FieldOfView` (0 = the game's), `[Lobby] LockCustomFov`.
* `[General] DebugKey` (F9) – host, in Granny's house: debug panel – *Spawn every item here* (one of every item, the game's own drop prefabs), *God mode for everybody* (nobody can be caught or die; sent to all players).
* `[Display] Resolution` (e.g. `1920x1080`, empty = the game's), `WindowMode` (0 fullscreen, 1 borderless window, 3 window, -1 = the game's).
* `[Network] TickRate` (default 20), `DisableSteam` (the game starts without Steam – see *Two copies on one PC*).
* `[Sync] MovementValidation`, `MaxSpeed`, `InteractionRadius`, `ReplicateSounds`.
* `[Voice] Enabled`, `Mode` (PushToTalk / Toggle / Always), `PushToTalkKey` (default V), `Microphone` (empty = Windows default),
  `MicVolume`, `MicThreshold` (Always mode), `Volume`, `Range` (metres). Steam is not needed. Everything can be set in the F1 settings.
* `[Scenes]` – scene names (normally no need to change).
* `[Advanced]` – lists of host-only scripts, forwarded triggers, synchronised fields and RPC methods,
  `UnsyncedSpawns` (clones only Granny's AI uses, never sent – noise targets).
  They let you tune the mod without recompiling when some part of the game is not synchronised.
* `[Debug] VerboseLogging = true` – lots of information in `BepInEx\LogOutput.log` (useful for bug reports).

---

## Two copies on one PC (testing)
1. Set `[Network] DisableSteam = true` in `BepInEx\config\pl.grannymp.coop.cfg`. The game then starts without Steam
   (the game's own Steam start-up is skipped – otherwise it restarts through the Steam client and a second copy quits);
   only Direct IP works, Steam lobbies, invites and DEV / Helper badges do not.
2. Start `Granny.exe` **twice, straight from the game folder** (not from Steam). Windowed is easier:
   `Granny.exe -screen-fullscreen 0 -screen-width 1280 -screen-height 720`.
3. Copy 1: F1 → Direct IP → *Host Direct IP*. Copy 2: F1 → join `127.0.0.1:27777`.

Both copies read the same config, so they have the same name – the host adds "(2)" to the second one.
In a session the game keeps
running when its window is not focused (both copies, and an alt-tabbed player in a normal game). The second copy writes its log to
`BepInEx\LogOutput.log.1`. Set `DisableSteam` back to `false` (and restart) to play over Steam again.

---

## Test status

The mod is compiled against the real interop libraries of this game version, and it is scanned for game methods stripped from the build.
The Direct IP layer was tested automatically (handshake, ordering, fragmentation, 25% packet loss).
**Gameplay itself still needs testing in the game.** If something is not synchronised, enable `VerboseLogging`, play until
the problem is visible (on every PC) and send `BepInEx\LogOutput.log`.

---

## Building from source

Requirements: Windows, .NET SDK 6+, Granny (Steam) with **BepInEx 6.0.0-be.785 IL2CPP** installed and the game started
once (BepInEx then generates `BepInEx\interop`, which the project compiles against – those DLLs are not in this repository).

1. Copy `GrannyMP.local.props.example` to `GrannyMP.local.props` and set `GameDir` to your Granny folder
   (or build with `dotnet build -c Release -p:GameDir="C:\...\Granny"`).
2. Run `build.bat` (or `dotnet build -c Release`). `GrannyMP.dll` and the bear-trap icon go straight to
   `<GameDir>\BepInEx\plugins\GrannyMP\`.
3. Character models (`.gmdl`) go to `<GameDir>\BepInEx\plugins\GrannyMP\models\` – they are not in the repository
   (third-party art); make them with the Model Tool.

The Model Tool (`tools\ModelTool`, .NET 8 WinForms): `dotnet publish -c Release`. Its built-in animations are embedded
from `tools\ModelTool\DefaultAnims\idle.fbx`, `walk.fbx`, `crouch.fbx` (Mixamo *Idle*, *Walking*, *Crouching Idle* –
not in the repository; put your own there, or the tool builds without built-in animations).

```
src/Core   Plugin.cs (BepInEx entry), Cfg.cs, Loc.cs (EN/PL), NetRunner.cs (loop), Patches.cs (Harmony), Util.cs
src/Net    Protocol.cs, Transport.cs, SteamNative.cs (steam_api64 flat API), SteamTransport.cs, UdpTransport.cs, Session.cs (lobby)
src/Sync   World.cs (registry + world replication), PlayerSync.cs, AiControl.cs, GameFlow.cs, Events.cs, GameRefs.cs,
           Spectator.cs, AudioStateSync.cs, MusicSync.cs, VoiceChat.cs, VoiceAudio.cs (winmm microphone / output, ADPCM)
src/UI     LobbyUI.cs (IMGUI: lobby, settings, HUD, name tags, chat)
```
