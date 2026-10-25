using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace GrannyMP
{
    public enum CoopMode : byte
    {
        /// <summary>Any player caught = everybody loses a day (like the original game).</summary>
        SharedDays = 0,
        /// <summary>A caught player becomes a ghost until the next day; the day is lost only when everyone is caught.</summary>
        LastSurvivor = 1,
        /// <summary>Config only (DefaultMode): "last survivor" with PvP on. In a session PvP is Session.PvpOn.</summary>
        PvP = 2,
    }

    internal static class Cfg
    {
        public static ConfigEntry<Language> Lang;
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<int> MaxPlayers;
        public static ConfigEntry<int> Port;
        public static ConfigEntry<string> Password;
        public static ConfigEntry<KeyCode> ToggleKey, DebugKey;
        public static ConfigEntry<KeyCode> ChatKey;
        public static ConfigEntry<int> TickRate;
        public static ConfigEntry<bool> DisableSteam;
        public static ConfigEntry<int> SteamLobbyType;
        public static ConfigEntry<bool> AllowJoinInProgress, LockFov;
        public static ConfigEntry<float> Fov;
        public static ConfigEntry<string> ScreenResolution;
        public static ConfigEntry<int> ScreenMode;
        public static ConfigEntry<bool> SpotCatchAll, SpotSingle;
        public static ConfigEntry<CoopMode> DefaultMode;
        public static ConfigEntry<float> PvpGrannySpeed;
        public static ConfigEntry<bool> PvpNightVision, PvpOwnBody;
        public static ConfigEntry<float> PvpCameraHeight, ModelSize;
        public static ConfigEntry<KeyCode> PvpLaughKey, PvpTrapKey, PvpGrabKey, PvpVoiceKey;
        public static ConfigEntry<bool> SpeedCheck;
        public static ConfigEntry<float> MaxSpeed;
        public static ConfigEntry<float> ProposeRadius;
        public static ConfigEntry<bool> ShowNametags;
        public static ConfigEntry<string> Character;
        public static ConfigEntry<bool> ReplicateSounds;
        public static ConfigEntry<bool> DebugLog;
        public static ConfigEntry<string> GameScene;
        public static ConfigEntry<string> MenuScene;
        public static ConfigEntry<string> IntroScene;
        public static ConfigEntry<string> EndScene;
        public static ConfigEntry<string> HostOnlyTypes;
        public static ConfigEntry<string> TriggerForwardTypes;
        public static ConfigEntry<string> StateSyncTypes;
        public static ConfigEntry<string> WorldFieldTypes, WorldFields, ClientAiFields;
        public static ConfigEntry<string> HostRpcMethods;
        public static ConfigEntry<string> AiRootTypes;
        public static ConfigEntry<string> PrefsExcludeRegex;
        public static ConfigEntry<string> TransientFieldRegex;
        public static ConfigEntry<string> PersonalTags;
        public static ConfigEntry<string> MirroredMusic;
        public static ConfigEntry<string> LocalForwardTypes;
        public static ConfigEntry<string> ExcludePathRegex;
        public static ConfigEntry<string> OwnedLocalTriggerTypes, UnsyncedSpawns;
        public static HashSet<string> OwnedLocalSet = new(), UnsyncedSpawnSet = new();
        public static ConfigEntry<bool> VoiceEnabled;
        public static ConfigEntry<KeyCode> VoiceKey;
        public static ConfigEntry<float> VoiceVolume, VoiceRange, MicGain, MicThreshold;
        public static ConfigEntry<VoiceMode> VoiceModeCfg;
        public static ConfigEntry<string> MicDevice;
        public static ConfigEntry<bool> SkipSplash;
        public static ConfigEntry<float> UiScale;
        public static HashSet<string> LocalForwardSet = new();
        public static ConfigEntry<int> AdvancedDefaultsVersion;
        const int CurrentAdvancedDefaults = 17;
        public static HashSet<string> PersonalTagSet = new();

        public static HashSet<string> HostOnlySet = new();
        public static HashSet<string> ForwardSet = new();
        public static HashSet<string> AiRootSet = new();
        public static HashSet<string> WorldFieldSet = new(), WorldFieldNameSet = new(), ClientAiFieldSet = new();
        /// <summary>type -> field names (null = all simple fields)</summary>
        public static Dictionary<string, HashSet<string>> StateSyncMap = new();
        public static List<(string type, string method)> HostRpcList = new();

        public static void Bind(ConfigFile c)
        {
            Lang = c.Bind("General", "Language", Language.English, "Mod language: English or Polish (can also be changed in the F1 window).");
            PlayerName = c.Bind("General", "PlayerName", "", "Your name in the lobby. Empty = Steam name or Windows user name.");
            ToggleKey = c.Bind("General", "LobbyKey", KeyCode.F1, "Opens/closes the multiplayer window.");
            DebugKey = c.Bind("General", "DebugKey", KeyCode.F9, "Host, in Granny's house: the debug panel (spawn every item, god mode for everybody).");
            ChatKey = c.Bind("General", "ChatKey", KeyCode.T, "Opens the in-game chat.");
            Character = c.Bind("General", "Character", PlayerModels.Default, "How the other players see you: the name of a model in BepInEx\\plugins\\GrannyMP\\models (male, female or a custom .gmdl).");
            ShowNametags = c.Bind("General", "ShowNametags", true, "Show names above other players.");
            ModelSize = c.Bind("General", "PlayerModelSize", 1.15f, new ConfigDescription("How tall the other players' characters are compared with how high their camera is (1 = the top of the head at the camera).", new AcceptableValueRange<float>(0.8f, 1.3f)));
            UiScale = c.Bind("General", "InterfaceScale", 0f, new ConfigDescription("Size of the mod's windows and texts. 0 = automatic (from the screen resolution: 100% at 1080p, 200% at 4K).", new AcceptableValueRange<float>(0f, 4f)));
            SkipSplash = c.Bind("General", "SkipIntroSplash", false, "Skip the DVloper splash screen when the game starts (straight to the menu).");

            MaxPlayers = c.Bind("Lobby", "MaxPlayers", 8, new ConfigDescription("Maximum number of players including the host.", new AcceptableValueRange<int>(2, 9)));
            Port = c.Bind("Lobby", "DirectIPPort", 27777, "UDP port used by Direct IP hosting.");
            Password = c.Bind("Lobby", "Password", "", "Optional lobby password (Direct IP and Steam). Can be set in the F1 window before hosting.");
            SteamLobbyType = c.Bind("Lobby", "SteamLobbyType", 1, "0 = private (invite only), 1 = friends only, 2 = public.");
            AllowJoinInProgress = c.Bind("Lobby", "AllowJoinInProgress", true, "Allow players to join while a game is running.");
            SpotCatchAll = c.Bind("Lobby", "CatchEverybodyInHidingSpot", true, "When Granny finds a player in a hiding spot (bed, coffin, car), the other players hiding in the same spot are caught too.");
            SpotSingle = c.Bind("Lobby", "OnePlayerPerHidingSpot", false, "Only one player can hide in a bed / coffin / car at a time.");
            LockFov = c.Bind("Lobby", "LockCustomFov", false, "Host: the players cannot use their own field of view – everybody plays with the game's one.");
            Fov = c.Bind("General", "FieldOfView", 0f, new ConfigDescription("Your field of view in Granny's house (0 = the game's own). The host can lock it in the server configuration.", new AcceptableValueRange<float>(0f, 120f)));
            ScreenResolution = c.Bind("Display", "Resolution", "", "The game's resolution, e.g. 1920x1080 (set in the F1 settings; empty = the game's own).");
            ScreenMode = c.Bind("Display", "WindowMode", -1, "0 = fullscreen, 1 = borderless window, 3 = window (set in the F1 settings; -1 = the game's own).");
            DefaultMode = c.Bind("Lobby", "DefaultMode", CoopMode.SharedDays, "SharedDays = everybody loses a day when anyone is caught; LastSurvivor = caught players spectate the others until the end of the day; PvP = one random player plays Granny, the others are survivors.");

            PvpGrannySpeed = c.Bind("PvP", "GrannySpeedFactor", 1f, new ConfigDescription("PvP: the Granny player moves at Granny's chase speed for the chosen difficulty (like in single player: easy 2.5, normal 3.8, hard 5, extreme 8.5 m/s) times this factor.", new AcceptableValueRange<float>(0.5f, 2f)));
            PvpCameraHeight = c.Bind("PvP", "GrannyCameraHeight", 0.9f, new ConfigDescription("PvP: the Granny player's camera height as a part of the height of Granny's eyes (1 = exactly at her eyes).", new AcceptableValueRange<float>(0.6f, 1.1f)));
            PvpOwnBody = c.Bind("PvP", "GrannySeesOwnBody", true, "PvP: the Granny player sees Granny's body (her head is folded away so it does not block the view).");
            PvpNightVision = c.Bind("PvP", "GrannyNightVision", true, "PvP: Granny sees in the dark – a soft light around the Granny player's eyes (only on their screen).");
            PvpLaughKey = c.Bind("PvP", "LaughKey", KeyCode.None, "Not used any more – the right mouse button makes Granny say a line or laugh.");
            PvpTrapKey = c.Bind("PvP", "TrapKey", KeyCode.B, "PvP, playing Granny: put a bear trap down.");
            PvpGrabKey = c.Bind("PvP", "GrabKey", KeyCode.Mouse0, "PvP, playing Granny: grab a survivor (or swing at the air).");
            PvpVoiceKey = c.Bind("PvP", "VoiceKey", KeyCode.Mouse1, "PvP, playing Granny: say one of her lines or laugh.");

            VoiceEnabled = c.Bind("Voice", "Enabled", true, "Proximity voice chat (own audio, works with Steam and Direct IP).");
            VoiceModeCfg = c.Bind("Voice", "Mode", VoiceMode.PushToTalk, "PushToTalk = hold the key, Toggle = press the key to switch the microphone on/off, Always = voice activation (sensitivity: MicThreshold).");
            VoiceKey = c.Bind("Voice", "PushToTalkKey", KeyCode.V, "Push to talk / toggle key.");
            MicDevice = c.Bind("Voice", "Microphone", "", "Microphone name (as shown in Windows). Empty = Windows default. Can be chosen in the F1 settings.");
            MicGain = c.Bind("Voice", "MicVolume", 1f, new ConfigDescription("Microphone volume (amplification).", new AcceptableValueRange<float>(0.1f, 8f)));
            MicThreshold = c.Bind("Voice", "MicThreshold", 0.02f, new ConfigDescription("Always mode: how loud you must speak for the microphone to open (0–0.25).", new AcceptableValueRange<float>(0f, 0.25f)));
            VoiceVolume = c.Bind("Voice", "Volume", 1f, new ConfigDescription("Volume of the other players' voices.", new AcceptableValueRange<float>(0f, 3f)));
            VoiceRange = c.Bind("Voice", "Range", 20f, new ConfigDescription("Distance (m) at which a voice fades out completely in Granny's house.", new AcceptableValueRange<float>(3f, 100f)));

            TickRate = c.Bind("Network", "TickRate", 20, new ConfigDescription("How many times per second state is sent.", new AcceptableValueRange<int>(5, 60)));
            DisableSteam = c.Bind("Network", "DisableSteam", false,
                "Start the game without Steam: the game's own Steam start-up (SteamManager) and the mod's Steam lobbies are skipped, only Direct IP works. " +
                "Use it to run two copies of the game on one PC for testing (host Direct IP in one, join 127.0.0.1:27777 in the other). Restart the game after changing it.");

            SpeedCheck = c.Bind("Sync", "MovementValidation", true, "The host rejects impossible client movement (teleports / speed hacks).");
            MaxSpeed = c.Bind("Sync", "MaxSpeed", 9f, "Maximum horizontal player speed (m/s) accepted by the host.");
            ProposeRadius = c.Bind("Sync", "InteractionRadius", 8f, "World changes within this radius of a client are treated as that client's actions and sent to the host.");
            ReplicateSounds = c.Bind("Sync", "ReplicateSounds", true, "Replicate sounds (footsteps, doors, Granny).");

            GameScene = c.Bind("Scenes", "GameScene", "Scene", "Scene with Granny's house.");
            MenuScene = c.Bind("Scenes", "MenuScene", "Menu", "Main menu scene.");
            IntroScene = c.Bind("Scenes", "IntroScene", "ForestScene", "Intro scene (forest).");
            EndScene = c.Bind("Scenes", "EndScene", "EndScene", "Escape / ending scene.");

            HostOnlyTypes = c.Bind("Advanced", "HostOnlyTypes",
                "EnemyAIGranny,EnemyEye,GrannyCloseSensor,grannyClosePrisondoor,GrannyBreak,stopGranny,spiderControll,spiderWallControll,spiderHitPlayer,SpiderBackoff,resetSpider," +
                "CrowControl,ratController,ratBite,momCrawl,MomTeleport,TeleportMonster,teleportAwayTrigger,SkeletonWanderer,manikinMove,AIfollow,NPCMovement," +
                "LittleSantaController,MomSpiderHead,MomSpiderHeadTilt,startMomHead,MomHead,GhostFlight,RandomBatFlight," +
                "RandomKeyPlace,RandomKofotPlace,RandomDunkPlace,fadeUpDownGrannyHunt",
                "AI / randomisation scripts that only run on the host (disabled on clients, their effect comes from the network).");
            TriggerForwardTypes = c.Bind("Advanced", "ForwardedTriggerTypes",
                "GrannyHear,grannySeePlayerTrigger,cameraSeeTrigger,KartongMakeSound,PlanksMakesSound,klockaTrigger,noiceObjectFalls,objectFallFloor,ObjectFallFloorDestroys," +
                "ObjectFallFloorMultiple,objectsFallFloorTavla,HandObjectFalls,ljusstakeFallFloar,teddyFalls,triggerFallFloor,spiderHuntPlayer,spiderTrigger,SpiderTrigger,spiderFoodTrigger,skullTrigger,batsTrigger,TriggerBats," +
                "momStartCrawl,teddyInVagga,deactivateSlendrina,carbumberTrigger,startMomSpiderTrigger,startMomSpiderTrigger2,crowArrowShot",
                "Triggers (noise, Granny sensors) that a client forwards to the host instead of running them locally.");
            StateSyncTypes = c.Bind("Advanced", "StateSyncTypes",
                "OpenCloseDoors:*;OpenCloseSmallDoors:*;OpenCloseKitchenDoor1:*;OpenCloseKitchenDoor2:*;OpenCloseLoda1:*;OpenCloseLoda2:*;OpenCloseKyl:*;OpenCloseOldDoor:*;" +
                "OpenCloseLeftOldDoor:*;OpenCloseBackdoor:*;openCloseBaklucka:*;OpenCloseDoorV:*;OpenCloseDoorVind:*;OpenCloseGarde1:*;openCloseMotorhuv:*;openCloseVitrindoorL:*;" +
                "openCloseVitrindoorR:*;openCloseVitrindoorVRL:*;openCloseVitrindoorVRR:*;prisonDoorOpenClose:*;lockerdoorscript:*;doorSlide:*;CheckExitDoor:*;haveObjects:*;" +
                "startNewDay:tavelbit1,tavelbit2,tavelbit3,tavelbit4,allaTavelbitarOnPlace,slendrinaAppeard;PickUp:kugg1OK,kugg2OK;CoffinsController:*;elevatorController:*;" +
                "DeliverShotgunParts:*;checkIronDoors:*;CandleOnOff:*;BearTrap:beartrapOn;BeartrapTrigger:*;organicBeartrap:*;freezeTrap:trapActivated,GrannyFreeze;CrowControl:burdoorIsOpen,crowGetShoot,crowNotShotAgain,CrowStartEat,shootInBur;MomSpiderHead:getShot;" +
                "spiderControll:spiderDead,foodTime,spiderStartEat;OpenMetalBox:*;placePlanka:*;placePlankaHole:*;skruvplatta:*;skruvplattaOutside:*;SkjutplattaDoor:*;tryckplatta:*;" +
                "vevaBrunnsVev:*;moveTunnellock:*;PlayHouseLock:*;remoteLocktrigger:*;openSecretDoorTrigger:*;startTheCar:*;checkTheCar:*;giljotinTrigger:*;" +
                // scripts on the player that keep world facts (the fuel tank is full, the winch handle is on the well) – synced field by field
                "fillingFuel:noMoreFill,tanklockAnimPlayed;openDoors:garageportLock;playerVevar:noMoreVev,vevInPlace;UseRemote:doorUnlocked;slendrinaAppear:SlendrinaAppear,fadeUpDown;teddyInVagga:fadeDown;" +
                "EnemyAIGranny:bastuswitchOn,bastuBomNere,bastuTimeOff,bastuTimer,grannyInBastu,playerStartCar,hitByGun,hitByPepper,hitByPepperStart,hitByArrow",
                "Type:field1,field2 or Type:* – bool/int/float fields replicated by the host. Separate with semicolons.");
            WorldFieldTypes = c.Bind("Advanced", "WorldFieldTypes",
                "CheckExitDoor,openDoors,haveObjects,prisonDoorOpenClose,resetDay,spiderControll,CrowControl,triggerFallFloor,CoffinsController,DeliverShotgunParts",
                "GameObjects referenced by fields of these scripts are registered for sync (active state, position). Do NOT add InventoryController/PickUp (their fields are hand models).");
            WorldFields = c.Bind("Advanced", "WorldFields",
                // what PickUp switches on / off when an item is used: car parts, cut cables, the cogwheels, the winch handle, the book…
                "PickUp.avklipptKabelCellar,PickUp.avklipptKabelVind,PickUp.KabelVind,PickUp.avklipptKabel,PickUp.fan,PickUp.fanCollider,PickUp.LampaDoor1,PickUp.LampaDoor2," +
                "PickUp.Bom,PickUp.DdoorLock,PickUp.topplockInPlace,PickUp.carbatteryInPlace,PickUp.sparkPlugInPlace,PickUp.sparkPlugCable,PickUp.meatOnPlate,PickUp.spiderTrigger," +
                "PickUp.specialkeyLock,PickUp.specialkeyDoor,PickUp.specialkeyInPlace,PickUp.wheelCrankInPlace,PickUp.woodenStickInPlace,PickUp.oldMomRigidbody,PickUp.bookInPlace," +
                "PickUp.StartCrawlMomTrigger,PickUp.plankaHole,PickUp.skruvPlatta,PickUp.skruvPlattaOutside,PickUp.kamera,PickUp.kameraBroken,PickUp.galler,PickUp.gallerColliders," +
                "PickUp.prisonDoor,PickUp.batteryOnPlace,PickUp.batterySpak,PickUp.playhouseDoor,PickUp.melonInPlace,PickUp.giljotin,PickUp.playHouseLucka,PickUp.kugg1inPlace," +
                "PickUp.kugg2inPlace,PickUp.brunnsvevInPlace,PickUp.extremeLockOn,PickUp.extremeLockOff,PickUp.CrossBowLocker,PickUp.AllSyringe,PickUp.luckaSpiderCellar," +
                "PickUp.ironDoorsHolder,PickUp.spiderCellarGallerHolder," +
                "teddyInVagga.slendrina,teddyInVagga.teddyTexture,teddyInVagga.glow,slendrinaAppear.slendrinaTexture,slendrinaAppear.slendrinaHairTexture,slendrinaAppear.particleSys," +
                "deactivateSlendrina.slendrina,fillingFuel.tanklockAnim,playerVevar.rope,playerVevar.winch,playerVevar.brunnsLjud,momStartCrawl.momCrawl,momStartCrawl.momHang," +
                "forwardCar.CarHitTriggers,reverseCar.CarHitTriggers,UseRemote.doorLockCollider,UseRemote.doorLockOpen,UseRemote.doorLockClosed,UseRemote.lockSound," +
                "tavelbitTrigger.tb1Ram,tavelbitTrigger.tb2Ram,tavelbitTrigger.tb3Ram,tavelbitTrigger.tb4Ram,EnemyAIGranny.grannyEyeColor",
                "Single script fields (Type.field) whose GameObject is registered for sync (active state, position) – for scripts like PickUp whose other fields are hand models.");
            ClientAiFields = c.Bind("Advanced", "ClientAiFields",
                "EnemyAIGranny.bastuswitchOn,EnemyAIGranny.bastuBomNere,EnemyAIGranny.bastuTimer,EnemyAIGranny.playerStartCar,CrowControl.CrowStartEat," +
                "EnemyAIGranny.hitByGun,EnemyAIGranny.hitByPepper,EnemyAIGranny.hitByPepperStart,MomSpiderHead.getShot,spiderControll.foodTime,EnemyAIGranny.hitByArrow",
                "Fields of host-only AI that a client's own game sets (the sauna lever, starting the car) – sent to the host. They must also be in StateSyncTypes.");
            HostRpcMethods = c.Bind("Advanced", "HostRpcMethods",
                "EnemyAIGranny.grannyHitByPepper,EnemyAIGranny.grannyHitByArrow,EnemyAIGranny.grannyHitByCar,EnemyAIGranny.grannyHitByGun,EnemyAIGranny.grannyFreeze," +
                "spiderControll.spiderIsDead,ratController.getShoot,explode.explodeNow",
                "Methods called on a client (e.g. hitting Granny) that are executed by the host.");
            AiRootTypes = c.Bind("Advanced", "AiTypes",
                "EnemyAIGranny,spiderControll,CrowControl,ratController,momCrawl,SkeletonWanderer,manikinMove,AIfollow,NPCMovement,LittleSantaController,MomSpiderHead",
                "AI roots whose position is synced from the host.");
            PrefsExcludeRegex = c.Bind("Advanced", "PlayerPrefsExcludeRegex",
                @"(?i)^(unity|screenmanager|sens|mouse|volume|music|sound|resolution|quality|vsync|fullscreen|graphics|fps|language|ads)",
                "PlayerPrefs keys the host does NOT send to clients (local settings).");
            TransientFieldRegex = c.Bind("Advanced", "TransientFieldRegex",
                @"(?i)(timer|moving|sound|play|ljud|time$|counter$|^text|highlight|ray|^temp|started|done$)",
                "Fields matching this are skipped for Type:* (short-lived state).");
            PersonalTags = c.Bind("Advanced", "PersonalItemTags", "flashlight,lykta",
                "Tags of items every player picks up for themselves (not synced): flashlight, lantern (lykta).");
            MirroredMusic = c.Bind("Advanced", "MirroredMusic",
                "EnemyAIGranny.GrannyHuntMusicHolder,EnemyAIGranny.GrannyHuntMusicHolderNightmare,EnemyAIGranny.GrannyHuntMusicHolderHalloween," +
                "EnemyAIGranny.GrannyHuntMusicHolderChristmas,EnemyAIGranny.teddyMusicHolder,MomSpiderHead.momSpiderHuntingMusic,AIfollow.HuntmusicHolder",
                "Music started by host-only AI, mirrored on the clients (Type.field of a GameObject with an AudioSource). Granny's chase music is heard only by the chased player.");
            LocalForwardTypes = c.Bind("Advanced", "LocalAndForwardedTriggerTypes", "triggerFallFloor,startMomSpiderTrigger,startMomSpiderTrigger2",
                "Forwarded triggers that a client ALSO runs locally (things that must physically happen under the client, e.g. the attic floor breaking).");
            ExcludePathRegex = c.Bind("Advanced", "ExcludePathRegex", @"(?i)(button|joystick|canvas|crosshair|mobile|handholdobjects|oldmom_hang#\d+/armature)",
                "Objects whose hierarchy path matches this are never synchronised (on-screen buttons, the items in the player's hand and other per-player things; the bones of Granny's mother hanging – her fall is simulated on every machine).");
            OwnedLocalTriggerTypes = c.Bind("Advanced", "OwnedLocalTriggerTypes", "ObjectFallFloorDestroys,ObjectFallFloorMultiple",
                "Forwarded triggers that a client ALSO runs locally when the object is simulated by that client (falling attic boards switch their collider off on the floor).");
            UnsyncedSpawns = c.Bind("Advanced", "UnsyncedSpawns", "TempNavObjects,NoiceObject,PlayerRagdoll",
                "Clones that are never sent to other players: noise targets only Granny's AI uses, the dead player's falling camera (PlayerRagdoll).");
            AdvancedDefaultsVersion = c.Bind("Advanced", "DefaultsVersion", 0,
                "Internal: version of the [Advanced] defaults. When a mod update brings new defaults, the [Advanced] lists are refreshed automatically.");
            // the characters' old default size is replaced by the new one (a size set by hand stays)
            if (AdvancedDefaultsVersion.Value < 11 && Math.Abs(ModelSize.Value - 1.08f) < 0.001f) ModelSize.Value = 1.15f;
            if (AdvancedDefaultsVersion.Value < CurrentAdvancedDefaults)
            {
                foreach (var e in new ConfigEntryBase[] { HostOnlyTypes, TriggerForwardTypes, StateSyncTypes, WorldFieldTypes, WorldFields, ClientAiFields, HostRpcMethods, AiRootTypes, PrefsExcludeRegex, TransientFieldRegex, PersonalTags, MirroredMusic, LocalForwardTypes, ExcludePathRegex, OwnedLocalTriggerTypes, UnsyncedSpawns })
                    e.BoxedValue = e.DefaultValue;
                AdvancedDefaultsVersion.Value = CurrentAdvancedDefaults;
            }
            DebugLog = c.Bind("Debug", "VerboseLogging", false, "Lots of diagnostic output in BepInEx/LogOutput.log (AI decisions, ownership, spawns, sounds, periodic status).");

            Rebuild();
        }

        static IEnumerable<string> Split(string s, params char[] sep) =>
            (s ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0);

        public static void Rebuild()
        {
            HostOnlySet = new HashSet<string>(Split(HostOnlyTypes.Value, ','));
            ForwardSet = new HashSet<string>(Split(TriggerForwardTypes.Value, ','));
            LocalForwardSet = new HashSet<string>(Split(LocalForwardTypes?.Value ?? "", ','));
            foreach (var x in LocalForwardSet) ForwardSet.Add(x);
            OwnedLocalSet = new HashSet<string>(Split(OwnedLocalTriggerTypes?.Value ?? "", ','));
            UnsyncedSpawnSet = new HashSet<string>(Split(UnsyncedSpawns?.Value ?? "", ','));
            AiRootSet = new HashSet<string>(Split(AiRootTypes.Value, ','));
            WorldFieldSet = new HashSet<string>(Split(WorldFieldTypes.Value, ','));
            PersonalTagSet = new HashSet<string>(Split(PersonalTags?.Value ?? "", ','), StringComparer.OrdinalIgnoreCase);
            // hand models must never be world objects
            WorldFieldSet.Remove("InventoryController"); WorldFieldSet.Remove("PickUp");
            WorldFieldNameSet = new HashSet<string>(Split(WorldFields?.Value ?? "", ','));
            ClientAiFieldSet = new HashSet<string>(Split(ClientAiFields?.Value ?? "", ','));
            StateSyncMap = new Dictionary<string, HashSet<string>>();
            foreach (var part in Split(StateSyncTypes.Value, ';'))
            {
                var i = part.IndexOf(':');
                var t = i < 0 ? part : part.Substring(0, i).Trim();
                var f = i < 0 ? "*" : part.Substring(i + 1).Trim();
                StateSyncMap[t] = f == "*" ? null : new HashSet<string>(Split(f, ','));
            }
            HostRpcList = new List<(string, string)>();
            foreach (var m in Split(HostRpcMethods.Value, ','))
            {
                var i = m.LastIndexOf('.');
                if (i > 0) HostRpcList.Add((m.Substring(0, i), m.Substring(i + 1)));
            }
        }
    }
}
