using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.AI;

namespace GrannyMP
{
    internal sealed class StateBinding
    {
        public object Wrapper;            // wrapper interop komponentu
        public Component Comp;            // the same component as Component (to check it is alive)
        public PropertyInfo Prop;
        public byte Kind;                 // 0 bool, 1 int, 2 float
        public string Name;
        public bool ClientMay;            // a field of host-only AI that a client's own game sets ([Advanced] ClientAiFields)

        public int Read()
        {
            var v = Prop.GetValue(Wrapper);
            switch (Kind)
            {
                case 0: return (bool)v ? 1 : 0;
                case 1: return (int)v;
                default: return BitConverter.SingleToInt32Bits((float)v);
            }
        }

        public void Write(int v)
        {
            switch (Kind)
            {
                case 0: Prop.SetValue(Wrapper, v != 0); break;
                case 1: Prop.SetValue(Wrapper, v); break;
                default: Prop.SetValue(Wrapper, BitConverter.Int32BitsToSingle(v)); break;
            }
        }

        public static bool Equal(byte kind, int a, int b)
        {
            if (kind != 2) return a == b;
            return Math.Abs(BitConverter.Int32BitsToSingle(a) - BitConverter.Int32BitsToSingle(b)) < 0.001f;
        }
    }

    internal sealed class NetObj
    {
        public uint Id;
        public GameObject Go;
        public Transform Tr;
        public string Path;
        public bool IsItem, IsAI, TrackPose, IsSpawned, HostOnly;
        public Transform OrigParent;

        public Animation Anim; public string[] Clips;
        public Animator Animator;
        public Collider[] Cols = Array.Empty<Collider>();
        public Collider[] ShapeCols;           // colliders measuring how near a player is (World.DistTo)
        public Vector3 ShapeMin, ShapeMax; public bool HasShape;
        public Collider[] AllCols;              // colliders of the object and its children (blocks taking someone else's item)
        public bool[] AllColsWas;
        public Renderer[] Rends = Array.Empty<Renderer>();
        public Behaviour[] Behs = Array.Empty<Behaviour>();
        public Rigidbody Rb; public bool OrigKinematic;
        public NavMeshAgent Agent;
        public List<StateBinding> States;

        // authoritative state (last sent by the host / last applied on a client)
        public bool AActive;
        public Vector3 APos; public Quaternion ARot = Quaternion.identity;
        public ushort AEnabled;
        public int AClip = -1; public float AClipTime;
        public int AAnimState; public float AAnimTime;
        public int[] AStates;
        public byte Owner = 255;

        // local animation tracking
        public int LClip = -1; public float LTime = float.MaxValue;
        public int LAnimState;

        public float DivergeSince, LastPoseSend, OwnerIdleSince, LastProposal;
        public Vector3 TPos; public Quaternion TRot; public bool HasTarget;
        public bool CollidersSuppressed;
        public bool PendingClaim;
        public byte AOwnerSent = 255;
        public bool IsPickable;                 // object the game destroys when picked up (tagged collider)
        public float RemoteDrivenUntil;
        public float ReleasedAt = -100f;
        public float QuietUntil;
        public bool IsWorld;                    // referenced by a field of a WorldFieldTypes script
        public bool LocalReleased;              // client: claimed because the local game released the body (falling attic boards)
        public bool IsContainer;                // parent of synced objects – only its active state is synced (random item placement groups)
        public bool TrackTag; public string ATag; // doors / drawers keep their open/closed state in the tag
        public float HoldUntil;
        public float LeftHandAt;               // client: when an owned item left this player's hand (thrown / dropped)                // client: owned body kept for a while (taken by an arrow, still at rest)
        public float GraceUntil; public byte GraceMask; // client: own proposal in flight – older host values for these fields are ignored
        public float LastClaim;
        public int Reverts; public byte LastDiffMask;
        public int EnabledFights; public bool LocalEnabled; // client: the local game keeps changing the enabled state – not a player action
        public bool StateOnly;                  // a script on the player keeping world facts – only its listed fields are synced
        public bool AStopped;                   // the animation was stopped half way (a fan whose cable was cut) – it stays where it is
        public bool LPlaying, LocalStopped; public float LLastTime;
        public float ALastTime;                 // host: the clip's time when it was last seen playing
        public sbyte Loaded = -1;               // a dropped weapon: 1 loaded, 0 empty, -1 not a weapon
        public bool PendingSpawn;               // client: local clone waiting for the host to assign an id
        public bool ExpectDestroy;              // destroyed because the network told us to
        public string PrefabName;
        public ulong StatesDirty;
        public int LocalClip = -1; public float LocalClipTime;
    }

    /// <summary>
    /// World registry and replication. The host sends changes (WorldDelta), clients propose changes
    /// they made locally near themselves (Proposal) – the host validates and rebroadcasts them.
    /// </summary>
    internal static class World
    {
        public const byte M_Active = 1, M_Pose = 2, M_Enabled = 4, M_Anim = 8, M_Animator = 16, M_Owner = 32, M_States = 64, M_Tag = 128;
        public const uint SpawnBit = 0x80000000;
        const byte DF_Forced = 1;                 // WorldDelta flag: apply everything (rejected proposal / claim) – not just changes

        public static readonly Dictionary<uint, NetObj> Objs = new();
        static readonly List<NetObj> _list = new();
        static readonly Dictionary<IntPtr, uint> _idByTr = new();
        static readonly Dictionary<uint, Transform> _trById = new();
        public static bool Registered;
        static readonly HashSet<uint> _registeredIds = new();
        static int _tick, _rolling;
        static uint _nextSpawnId = SpawnBit | 1;
        static readonly HashSet<IntPtr> _knownRoots = new();
        // clones the client's game made while it could not propose them (the player already counts as caught when the
        // game drops the held item during the catch) – proposed when the spectator drop window opens
        static readonly Dictionary<IntPtr, (GameObject go, float at)> _heldBackRoots = new();
        static float _lastSpawnScan;
        static Dictionary<string, GameObject> _prefabCache;
        public static bool Applying;              // true while applying network state (prevents echoes)
        public static float SuppressProposalsUntil;
        /// <summary>Client: report locally created clones even while proposals are suppressed (dropping the held item).</summary>
        public static float AllowLocalSpawnsUntil;
        /// <summary>Client: until then the host's placement of items wins over the local game's (random placement at start).</summary>
        public static float StartupUntil;
        static readonly NetWriter _dw = new();
        static Regex _transient;

        // ======================= rejestracja =======================
        public static void Clear()
        {
            _disabledByUs.Clear(); _collidersOffByUs.Clear(); PolledCols.Clear(); Events.ClearPolled();
            Objs.Clear(); _list.Clear(); _idByTr.Clear(); _trById.Clear(); _knownRoots.Clear(); _heldBackRoots.Clear(); _byRb.Clear();
            Registered = false; _nextSpawnId = SpawnBit | 1; _prefabCache = null;
        }

        public static uint IdOf(Transform t)
        {
            while (U.Alive(t))
            {
                if (_idByTr.TryGetValue(t.Pointer, out var id)) return id;
                t = t.parent;
            }
            return 0;
        }

        /// <summary>Nearest registered network object (itself or an ancestor).</summary>
        public static bool TryGetObj(uint id, out NetObj o) => Objs.TryGetValue(id, out o);
        public static bool IsPendingId(uint id) => id != 0 && Objs.TryGetValue(id, out var o) && o.PendingSpawn;

        /// <summary>Object currently moved by another player or by network corrections (its sounds come from the network).</summary>
        public static bool IsDrivenRemotely(NetObj o)
        {
            if (o == null || o.HostOnly || o.IsContainer) return false;
            if (o.Owner != 255 && o.Owner != Session.LocalId) return true;
            float now = Time.unscaledTime;
            return o.HasTarget || now < o.RemoteDrivenUntil + 0.3f || now < o.QuietUntil;
        }

        /// <summary>The last position the network gave this object (the host's, for Granny on a client).</summary>
        public static bool TryGetNetPos(Transform t, out Vector3 pos)
        {
            pos = default;
            if (!U.Alive(t) || !_idByTr.TryGetValue(t.Pointer, out var id) || !Objs.TryGetValue(id, out var o)) return false;
            pos = o.APos; return true;
        }

        public static NetObj NetObjOf(Transform t)
        {
            while (U.Alive(t))
            {
                // containers are only groups – sounds / triggers of their children keep their own policy
                if (_idByTr.TryGetValue(t.Pointer, out var id) && Objs.TryGetValue(id, out var o) && !o.IsContainer) return o;
                t = t.parent;
            }
            return null;
        }

        /// <summary>Client: physics body synced by pose – simulated only by its owner, kinematic everywhere else.</summary>
        static bool PhysBody(NetObj o) => U.Alive(o.Rb) && o.TrackPose && !o.HostOnly && !o.IsAI && !o.IsContainer;

        /// <summary>Client: the object (or its network parent) is simulated by this client's physics right now.</summary>
        public static bool IsLocallySimulated(Transform t)
        {
            var o = NetObjOf(t);
            return o != null && Session.IsClient && o.Owner == Session.LocalId;
        }

        public static uint ExactIdOf(Transform t) => U.Alive(t) && _idByTr.TryGetValue(t.Pointer, out var id) ? id : 0;
        public static Transform TrOf(uint id) => _trById.TryGetValue(id, out var t) && U.Alive(t) ? t : null;

        public static void Register(UnityEngine.SceneManagement.Scene scene)
        {
            Clear();
            _transient = new Regex(Cfg.TransientFieldRegex.Value);
            var all = new List<Transform>(8192);
            var paths = new Dictionary<IntPtr, string>();
            foreach (var root in scene.GetRootGameObjects())
            {
                _knownRoots.Add(root.Pointer);
                Walk(root.transform, "", paths, all);
            }
            GameRefs.Refresh();
            var player = GameRefs.PlayerRoot;
            var cam = GameRefs.PlayerCam;

            // korzenie AI
            var aiRoots = new List<Transform>();
            foreach (var t in all)
            {
                foreach (var mb in t.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    var tn = U.Il2CppTypeName(mb);
                    if (Cfg.AiRootSet.Contains(tn)) { aiRoots.Add(t); break; }
                }
            }

            // Hiding-spot cameras (bed, wardrobe, coffin), player animation rigs etc. are local to each player.
            var excludeRx = new Regex(string.IsNullOrEmpty(Cfg.ExcludePathRegex.Value) ? "$^" : Cfg.ExcludePathRegex.Value);
            bool Excluded(Transform t) => !U.Alive(t) || U.IsUI(t) || U.IsUnder(t, player) || (U.Alive(cam) && U.IsUnder(t, cam))
                                          || (paths.TryGetValue(t.Pointer, out var pth) && excludeRx.IsMatch(pth));
            bool UnderAI(Transform t) => aiRoots.Any(a => U.IsUnder(t, a));

            // objects referenced by fields of "world" scripts
            var itemSet = new HashSet<IntPtr>();
            var worldSet = new HashSet<IntPtr>();
            var fieldIds = new Dictionary<IntPtr, string>();   // object -> "Type.field" (stable regardless of hierarchy)
            var typeCounter = new Dictionary<string, int>();
            var trByPtr = all.ToDictionary(t => t.Pointer, t => t);
            foreach (var t in all)
            {
                foreach (var mb in t.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    var tn = U.Il2CppTypeName(mb);
                    bool worldType = Cfg.WorldFieldSet.Contains(tn);
                    var type = U.GameType(tn); if (type == null) continue;
                    object w; try { w = U.Wrap(type, mb.Pointer); } catch { continue; }
                    typeCounter.TryGetValue(tn, out var inst); typeCounter[tn] = inst + 1;
                    foreach (var p in FieldProps(type))
                    {
                        if (p.PropertyType != typeof(GameObject) && p.PropertyType != typeof(Transform)) continue;
                        try
                        {
                            Transform target = p.PropertyType == typeof(GameObject) ? (p.GetValue(w) as GameObject)?.transform : p.GetValue(w) as Transform;
                            if (!U.Alive(target) || !trByPtr.ContainsKey(target.Pointer)) continue;
                            // every object referenced by a script field gets a stable id ("Type#n.field"),
                            // so random item placement / re-parenting does not change ids between machines
                            if (worldType || Cfg.WorldFieldNameSet.Contains(tn + "." + p.Name)) worldSet.Add(target.Pointer);
                            if (!fieldIds.ContainsKey(target.Pointer)) fieldIds[target.Pointer] = $"{tn}#{inst}.{p.Name}";
                        }
                        catch { }
                    }
                }
            }

            // objects referenced by fields: id from the field name (random placement does not change the id)
            var usedFieldIds = new HashSet<uint>();
            foreach (var kv in fieldIds)
            {
                uint fid = U.Fnv("field:" + kv.Value);
                if (fid == 0 || usedFieldIds.Contains(fid)) continue;
                if (_trById.TryGetValue(fid, out var other) && other.Pointer != kv.Key) continue; // collision – keep the path id
                usedFieldIds.Add(fid);
                if (_idByTr.TryGetValue(kv.Key, out var oldId)) _trById.Remove(oldId);
                _idByTr[kv.Key] = fid; _trById[fid] = trByPtr[kv.Key];
            }

            int personal = 0, stateOnly = 0;
            foreach (var t in all)
            {
                if (Excluded(t))
                {
                    // scripts on the player / on-screen buttons that keep world facts (a cogwheel is in place, the fuel
                    // tank is full, the remote opened its lock): every machine has the same script – only the listed fields are synced
                    if (StatesOf(t, explicitOnly: true) is { } ps)
                    {
                        var so = new NetObj { Id = _idByTr[t.Pointer], Go = t.gameObject, Tr = t, Path = paths[t.Pointer], StateOnly = true, States = ps, OrigParent = t.parent };
                        Snapshot(so);
                        Objs[so.Id] = so; _list.Add(so); stateOnly++;
                    }
                    continue;
                }
                if (IsPersonal(t)) { personal++; continue; } // flashlight / lantern: every player takes their own
                bool underAI = UnderAI(t);
                // hiding cameras / player rigs – but an animation a world script plays stays synced (the garage gate is
                // animated on Garage_EscapeTunnel, which also holds the escape tunnel's cameras)
                // (not a camera rig itself – the window jump's camera parents are also openDoors fields: everybody got the camera)
                if (!underAI && !itemSet.Contains(t.Pointer) && U.HasCameraBelow(t) && !(worldSet.Contains(t.Pointer) && t.GetComponent<Animation>() != null && !CameraRig(t))) continue;
                bool isAIRoot = aiRoots.Any(a => a.Pointer == t.Pointer);
                var anim = t.GetComponent<Animation>();
                var animator = t.GetComponent<Animator>();
                var rb = t.GetComponent<Rigidbody>();
                bool isItem = rb != null && !rb.isKinematic && !underAI;   // physics props (chairs, boxes, vases…)
                bool world = worldSet.Contains(t.Pointer);
                bool pickable = !underAI && IsPickableCandidate(t);
                bool body = rb != null && !underAI && !AnimatedChain(t); // e.g. boards / planks a script releases later (not parts moved by an animation)
                // a part of an animated object that a forwarded trigger releases (the car's rear bumper falls off when
                // somebody walks into it): known from the start, its position is synced once it is released (HostTick)
                bool released = rb != null && !underAI && !body && HasForwardedScript(t);
                var states = StatesOf(t);
                if (!(isItem || world || pickable || body || released || anim != null || animator != null || states != null || isAIRoot)) continue;

                var o = new NetObj
                {
                    Id = _idByTr[t.Pointer], Go = t.gameObject, Tr = t, Path = paths[t.Pointer],
                    IsItem = isItem && !isAIRoot, IsAI = isAIRoot, HostOnly = underAI || isAIRoot,
                    TrackPose = isItem || isAIRoot || pickable || body || (world && (rb != null || !AnimatedChain(t))), /* world rigidbodies, e.g. attic floor boards */ OrigParent = t.parent, IsPickable = pickable,
                    Anim = anim, Animator = animator, Rb = rb, States = states, IsWorld = world,
                };
                if (rb != null) o.OrigKinematic = rb.isKinematic;
                o.TrackTag = !o.HostOnly && (anim != null || states != null);
                if (isAIRoot) o.Agent = t.GetComponent<NavMeshAgent>();
                if (anim != null)
                {
                    int n = anim.GetStateCount();
                    o.Clips = new string[Math.Min(n, 250)];
                    for (int i = 0; i < o.Clips.Length; i++) o.Clips[i] = anim.GetStateAtIndex(i)?.name ?? "";
                }
                o.Cols = t.GetComponents<Collider>().ToArray().Take(8).ToArray();
                o.Rends = t.GetComponents<Renderer>().ToArray().Take(4).ToArray();
                o.Behs = t.GetComponents<NavMeshObstacle>().ToArray().Select(x => (Behaviour)x).Take(3).ToArray(); // bit 15 = dynamic body
                Snapshot(o);
                o.LClip = o.AClip; o.LTime = o.AClipTime;
                Objs[o.Id] = o; _list.Add(o);
                if (rb != null) _byRb[rb.Pointer] = o;
            }

            // parents of synced objects, e.g. RandomPlace1..5: startNewDay activates ONE of these groups at random,
            // which decides where the items lie – only their active state is synced (the host's choice wins)
            int containers = 0;
            var regPtrs = new HashSet<IntPtr>(_list.Select(x => x.Tr.Pointer));
            var notGroup = new HashSet<IntPtr>();
            foreach (var o in _list.ToArray())
            {
                if (o.HostOnly || o.IsAI || o.StateOnly) continue;
                for (var p = o.Tr.parent; p != null; p = p.parent)
                {
                    if (regPtrs.Contains(p.Pointer)) continue;
                    if (notGroup.Contains(p.Pointer)) break;
                    // never groups holding hiding-spot cameras / player rigs / AI (their activity is per player or host-only)
                    if (Excluded(p) || UnderAI(p) || U.HasCameraBelow(p)) { notGroup.Add(p.Pointer); break; }
                    if (!_idByTr.TryGetValue(p.Pointer, out var pid) || !paths.TryGetValue(p.Pointer, out var ppath)) break;
                    regPtrs.Add(p.Pointer);
                    var c = new NetObj { Id = pid, Go = p.gameObject, Tr = p, Path = ppath, IsContainer = true, OrigParent = p.parent };
                    Snapshot(c);
                    Objs[c.Id] = c; _list.Add(c); containers++;
                }
            }

            Registered = true;
            _registeredIds.Clear(); foreach (var x in _list) _registeredIds.Add(x.Id);
            if (Session.IsClient) ApplyClientRules(all);
            try { MakeTemplates(); } catch (Exception e) { Log.Debug("spawn templates: " + e.Message); }
            Log.Info($"World: registered {_list.Count} network objects (of {all.Count} in scene), AI: {aiRoots.Count}, physics: {_list.Count(o => o.IsItem)}, pickables: {_list.Count(o => o.IsPickable)}, groups: {containers}, player scripts: {stateOnly}, personal (not synced): {personal}");
        }

        // ======================= client rules (without patching small methods) =======================
        static readonly List<Behaviour> _disabledByUs = new();
        static readonly List<Collider> _collidersOffByUs = new();
        public static readonly HashSet<IntPtr> PolledCols = new();

        static bool DefinesTrigger(Type t) =>
            t != null && (t.GetMethod("OnTriggerEnter", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, new[] { typeof(Collider) }, null) != null
                       || t.GetMethod("OnTriggerStay", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, new[] { typeof(Collider) }, null) != null);

        static void DisableTriggerColliders(GameObject go)
        {
            foreach (var c in go.GetComponents<Collider>())
                if (c != null && c.isTrigger && c.enabled) { c.enabled = false; _collidersOffByUs.Add(c); PolledCols.Add(c.Pointer); }
        }

        /// <summary>
        /// Client: AI/randomisation scripts are disabled (they run only on the host). Triggers that could not be
        /// patched safely are disabled, and noise triggers are polled manually and forwarded to the host.
        /// </summary>
        static void ApplyClientRules(List<Transform> all)
        {
            int dis = 0, polled = 0;
            foreach (var t in all)
            {
                if (!U.Alive(t)) continue;
                foreach (var mb in t.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    var tn = U.Il2CppTypeName(mb);
                    if (Cfg.HostOnlySet.Contains(tn))
                    {
                        if (mb.enabled) { mb.enabled = false; _disabledByUs.Add(mb); dis++; }
                        if (tn == "EnemyAIGranny" && !Patches.Has(tn, "OnTriggerStay") && !Patches.Has(tn, "OnTriggerEnter")) DisableTriggerColliders(t.gameObject);
                        var type = U.GameType(tn);
                        bool patched = Patches.Has(tn, "OnTriggerEnter") || Patches.Has(tn, "OnTriggerStay");
                        if (DefinesTrigger(type) && !patched) DisableTriggerColliders(t.gameObject);
                    }
                    else if (Cfg.ForwardSet.Contains(tn))
                    {
                        var type = U.GameType(tn);
                        bool patched = Patches.Has(tn, "OnTriggerEnter") || Patches.Has(tn, "OnTriggerStay");
                        if (DefinesTrigger(type) && !patched)
                        {
                            foreach (var c in t.GetComponents<Collider>())
                            {
                                if (c == null || !c.isTrigger) continue;
                                Events.AddPolledTrigger(mb, c);
                                if (c.enabled) { c.enabled = false; _collidersOffByUs.Add(c); }
                                PolledCols.Add(c.Pointer);
                                polled++;
                            }
                        }
                    }
                }
            }
            // physics bodies are simulated by the host (or by the player who moves them) – here they only follow
            // the network; two physics simulations of the same chair/key fighting each other made items jitter
            int kin = 0;
            foreach (var o in _list)
                if (PhysBody(o) && !o.Rb.isKinematic) { o.Rb.isKinematic = true; kin++; }
            Log.Info($"Client: disabled {dis} host-only scripts, {polled} triggers polled manually, {kin} bodies follow the host");
        }

        /// <summary>After the session ends, re-enable components disabled by the mod (single player keeps working).</summary>
        public static void RestoreClientRules()
        {
            foreach (var b in _disabledByUs) if (U.Alive(b)) b.enabled = true;
            foreach (var c in _collidersOffByUs) if (U.Alive(c)) c.enabled = true;
            foreach (var o in _list) if (PhysBody(o) && o.Owner != Session.LocalId) try { o.Rb.isKinematic = o.OrigKinematic; } catch { }
            _disabledByUs.Clear(); _collidersOffByUs.Clear(); PolledCols.Clear();
            Events.ClearPolled();
        }

        static void Walk(Transform t, string parentPath, Dictionary<IntPtr, string> paths, List<Transform> all)
        {
            // index among siblings with the same name → stable path
            int same = 0;
            var p = t.parent;
            if (p != null)
            {
                int idx = t.GetSiblingIndex();
                for (int i = 0; i < idx; i++) if (p.GetChild(i).name == t.name) same++;
            }
            var path = parentPath + "/" + t.name + "#" + same;
            uint id = U.Fnv(path);
            int salt = 0;
            while (_trById.ContainsKey(id) || id == 0) id = U.Fnv(path + "~" + (++salt));
            _idByTr[t.Pointer] = id; _trById[id] = t; paths[t.Pointer] = path;
            all.Add(t);
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), path, paths, all);
        }

        static readonly HashSet<string> _plainTags = new() { "Untagged", "MainCamera", "Player", "EditorOnly", "Respawn", "Finish", "GameController" };

        /// <summary>Item every player picks up for themselves (flashlight, lantern) – the object or one of its parents has a personal tag.</summary>
        static bool IsPersonal(Transform t)
        {
            if (Cfg.PersonalTagSet.Count == 0) return false;
            try
            {
                for (var x = t; x != null; x = x.parent)
                    if (Cfg.PersonalTagSet.Contains(x.gameObject.tag)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>Pickable world item: has a collider + renderer and a game tag (PickUp.Update identifies items by tag/name).</summary>
        static bool IsPickableCandidate(Transform t)
        {
            try
            {
                var tag = t.gameObject.tag;
                if (string.IsNullOrEmpty(tag) || _plainTags.Contains(tag)) return false;
                if (t.GetComponent<Collider>() == null) return false;
                return t.GetComponent<Renderer>() != null || U.ChildComp<Renderer>(t) != null;
            }
            catch { return false; }
        }

        static bool HasHaveFlag(Type invType, string field)
        {
            return invType.GetProperty("have" + field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) != null
                || field.StartsWith("new", StringComparison.Ordinal) == false && invType.Name == "PickUp" && invType.GetProperty("new" + field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) != null;
        }

        static readonly Dictionary<Type, List<PropertyInfo>> _fieldProps = new();
        /// <summary>Interop properties that are real fields (they have NativeFieldInfoPtr_X).</summary>
        public static List<PropertyInfo> FieldProps(Type t)
        {
            if (_fieldProps.TryGetValue(t, out var l)) return l;
            l = new List<PropertyInfo>();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!p.CanRead || !p.CanWrite) continue;
                if (t.GetField("NativeFieldInfoPtr_" + p.Name, BindingFlags.NonPublic | BindingFlags.Static) == null) continue;
                l.Add(p);
            }
            _fieldProps[t] = l;
            return l;
        }

        /// <summary>Synced script fields ([Advanced] StateSyncTypes) of the scripts on <paramref name="t"/>, null when none.</summary>
        static List<StateBinding> StatesOf(Transform t, bool explicitOnly = false)
        {
            List<StateBinding> states = null;
            foreach (var mb in t.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                var tn = U.Il2CppTypeName(mb);
                if (!Cfg.StateSyncMap.TryGetValue(tn, out var fields)) continue;
                if (explicitOnly && fields == null) continue;
                var bs = MakeBindings(tn, mb, fields);
                if (bs.Count > 0) (states ??= new List<StateBinding>()).AddRange(bs);
            }
            return states;
        }

        static List<StateBinding> MakeBindings(string typeName, MonoBehaviour mb, HashSet<string> fields)
        {
            _transient ??= new Regex(Cfg.TransientFieldRegex.Value);
            var res = new List<StateBinding>();
            var type = U.GameType(typeName);
            if (type == null) return res;
            object w;
            try { w = U.Wrap(type, mb.Pointer); } catch { return res; }
            foreach (var p in FieldProps(type))
            {
                byte kind;
                if (p.PropertyType == typeof(bool)) kind = 0;
                else if (p.PropertyType == typeof(int)) kind = 1;
                else if (p.PropertyType == typeof(float)) kind = 2;
                else continue;
                if (fields != null) { if (!fields.Contains(p.Name)) continue; }
                else if (_transient.IsMatch(p.Name)) continue;
                res.Add(new StateBinding { Wrapper = w, Comp = mb, Prop = p, Kind = kind, Name = typeName + "." + p.Name, ClientMay = Cfg.ClientAiFieldSet.Contains(typeName + "." + p.Name) });
                if (res.Count >= 40) break;
            }
            return res;
        }

        // ======================= reading state =======================
        static void Snapshot(NetObj o)
        {
            o.AActive = o.Go.activeSelf;
            o.APos = o.Tr.position; o.ARot = o.Tr.rotation;
            if (o.TrackTag) o.ATag = ReadTag(o);
            o.AEnabled = ReadEnabled(o);
            ReadAnim(o, out o.AClip, out o.AClipTime);
            if (o.AClip < 0) { o.AClip = -1; o.AClipTime = 0; }
            if (U.Alive(o.Animator)) { try { var s = o.Animator.GetCurrentAnimatorStateInfo(0); o.AAnimState = s.fullPathHash; o.AAnimTime = s.normalizedTime; } catch { } }
            o.LAnimState = o.AAnimState;
            if (o.States != null)
            {
                o.AStates = new int[o.States.Count];
                for (int i = 0; i < o.States.Count; i++) { try { o.AStates[i] = o.States[i].Read(); } catch { } }
            }
        }

        static string ReadTag(NetObj o) { try { return o.Go.tag ?? ""; } catch { return ""; } }

        static void WriteTag(NetObj o, string tag)
        {
            if (string.IsNullOrEmpty(tag)) return;
            TagSync.Applying = true;
            try { if (o.Go.tag != tag) o.Go.tag = tag; } catch (Exception e) { Log.Debug($"tag {o.Path}: {e.Message}"); }
            finally { TagSync.Applying = false; }
        }

        /// <summary>The interaction sync changed / applied this object's tag – the world sync takes it as the agreed state.</summary>
        public static void NoteLocalTag(uint id, string tag)
        {
            if (Objs.TryGetValue(id, out var o) && o.TrackTag) { o.ATag = tag; o.DivergeSince = 0; }
        }

        /// <summary>The interaction sync switched / applied this object on or off – the agreed state now.</summary>
        public static void NoteLocalActive(uint id, bool on)
        {
            if (Objs.TryGetValue(id, out var o) && !o.HostOnly) { o.AActive = on; o.DivergeSince = 0; }
        }

        static ushort ReadEnabled(NetObj o)
        {
            ushort bits = 0; int b = 0;
            foreach (var c in o.Cols)
            {
                bool on = PolledCols.Count > 0 && U.Alive(c) && PolledCols.Contains(c.Pointer) ? (o.AEnabled & (1 << b)) != 0 : U.Alive(c) && c.enabled;
                if (on) bits |= (ushort)(1 << b); b++;
            }
            foreach (var r in o.Rends) { if (U.Alive(r) && r.enabled) bits |= (ushort)(1 << b); b++; }
            foreach (var x in o.Behs) { if (U.Alive(x) && x.enabled) bits |= (ushort)(1 << b); b++; }
            // bit 15: the body is simulated (not kinematic) on the host – so clients can push it too
            if (U.Alive(o.Rb) && !o.HostOnly)
            {
                if (Session.IsHost) { if (!o.OrigKinematic) bits |= DynBit; }
                else bits |= (ushort)(o.AEnabled & DynBit);
            }
            return bits;
        }

        static void WriteEnabled(NetObj o, ushort bits)
        {
            int b = 0;
            foreach (var c in o.Cols) { if (U.Alive(c) && !o.CollidersSuppressed && !PolledCols.Contains(c.Pointer)) c.enabled = (bits & (1 << b)) != 0; b++; }
            foreach (var r in o.Rends) { if (U.Alive(r)) r.enabled = (bits & (1 << b)) != 0; b++; }
            foreach (var x in o.Behs) { if (U.Alive(x)) x.enabled = (bits & (1 << b)) != 0; b++; }
            if (Session.IsClient && U.Alive(o.Rb) && !o.HostOnly && !o.IsAI)
            {
                // only remembered: a body is simulated here only while this client owns it (pushing it, falling boards)
                bool dyn = (bits & DynBit) != 0;
                if (o.OrigKinematic == dyn && o.Owner != Session.LocalId)
                {
                    o.OrigKinematic = !dyn;
                    if (dyn) o.IsItem = true; // released on the host (fallen boards…) – now a normal physics object
                }
            }
        }
        const ushort DynBit = 1 << 15;

        /// <summary>Currently playing legacy Animation clip (index) and its time.</summary>
        static void ReadAnim(NetObj o, out int clip, out float time)
        {
            clip = -1; time = 0;
            if (!U.Alive(o.Anim) || o.Clips == null) return;
            if (!o.Anim.isPlaying) return;
            float bestW = -1;
            for (int i = 0; i < o.Clips.Length; i++)
            {
                var name = o.Clips[i];
                if (string.IsNullOrEmpty(name) || !o.Anim.IsPlaying(name)) continue;
                var st = o.Anim[name];
                if (st == null) continue;
                if (st.weight > bestW) { bestW = st.weight; clip = i; time = st.time; }
            }
        }

        // ======================= loop =======================
        public static void Tick()
        {
            if (!Registered || !Session.Active || !GameRefs.InGameScene) return;
            _tick++;
            bool readStates = _tick % 3 == 0;
            if (Session.IsHost) HostTick(readStates);
            else ClientTick(readStates);
        }

        // ---------- HOST ----------
        static void HostTick(bool readStates)
        {
            var w = BeginDelta();
            int count = 0;
            float now = Time.unscaledTime;
            int rollPer = Math.Max(1, _list.Count / (Cfg.TickRate.Value * 4));
            var playerRoot = GameRefs.PlayerRoot;

            for (int i = 0; i < _list.Count; i++)
            {
                var o = _list[i];
                if (!U.Alive(o.Go)) { HostObjectDestroyed(o); _list.RemoveAt(i); i--; continue; }

                // items held/moved by the host
                // the game switched a body between kinematic and simulated (boards released, planks fixed in place…)
                if (o.Owner == 255 && U.Alive(o.Rb) && !o.HostOnly && !o.IsAI && o.OrigKinematic != o.Rb.isKinematic)
                {
                    o.OrigKinematic = o.Rb.isKinematic;
                    if (!o.OrigKinematic)
                    {
                        o.IsItem = true;
                        // a part of an animated object (the car's rear bumper falling off) was not tracked so far
                        if (!o.TrackPose) { o.TrackPose = true; Log.Debug("Released by a script, its position is synced from now on: " + o.Path); }
                    }
                }
                if (o.IsItem && (o.Owner == 255 || o.Owner == 0))
                {
                    bool held = U.IsUnder(o.Tr, playerRoot);
                    if (held && o.Owner == 255) { o.Owner = 0; }
                    else if (!held && o.Owner == 0)
                    {
                        bool resting = !U.Alive(o.Rb) || o.Rb.isKinematic || o.Rb.IsSleeping() || !o.Go.activeInHierarchy;
                        if (resting) { if (o.OwnerIdleSince == 0) o.OwnerIdleSince = now; else if (now - o.OwnerIdleSince > 1f) { o.Owner = 255; o.OwnerIdleSince = 0; } }
                        else o.OwnerIdleSince = 0;
                    }
                }
                bool remoteOwned = o.Owner != 255 && o.Owner != 0;
                if (remoteOwned && o.IsItem && U.IsUnder(o.Tr, playerRoot)) { Session.Notify(L.T("Another player is holding this item!", "Ten przedmiot trzyma inny gracz!"), false); ForceUnhold(o); }

                byte mask = Diff(o, readStates, remoteOwned, now);
                if (o.Owner != o.AOwnerSent) mask |= M_Owner;
                if (mask == 0 && ((i + _rolling) % _list.Count) < rollPer) mask = FullMask(o);
                if (mask == 0) continue;
                WriteObj(w, o, mask);
                o.AOwnerSent = o.Owner;
                if (++count >= 300 || w.Len > 14000) { FlushDelta(w, count); w = BeginDelta(); count = 0; }
            }
            _rolling = (_rolling + rollPer) % Math.Max(1, _list.Count);
            FlushDelta(w, count);
            SendAiPoses();
            ScanSpawns();
        }

        /// <summary>Detects changes against the authoritative state and updates the cache. Returns the change mask.</summary>
        static byte Diff(NetObj o, bool readStates, bool skipOwnedFields, float now)
        {
            byte mask = 0;
            if (o.StateOnly) return readStates ? DiffStates(o) : (byte)0;
            if (!skipOwnedFields)
            {
                var act = o.Go.activeSelf;
                if (act != o.AActive) { o.AActive = act; mask |= M_Active; }
                if (o.TrackPose && !o.IsAI)
                {
                    var p = o.Tr.position; var r = o.Tr.rotation;
                    if ((p - o.APos).sqrMagnitude > 0.0004f || Quaternion.Angle(r, o.ARot) > 1.5f)
                    {
                        // moving bodies are sent at the full tick rate – clients only interpolate them
                        float minGap = U.Alive(o.Rb) && !o.Rb.isKinematic ? 0.045f : 0.1f;
                        if (now - o.LastPoseSend > minGap) { o.APos = p; o.ARot = r; o.LastPoseSend = now; mask |= M_Pose; }
                    }
                }
                var en = ReadEnabled(o);
                if (en != o.AEnabled) { o.AEnabled = en; mask |= M_Enabled; }
            }
            if (U.Alive(o.Anim))
            {
                ReadAnim(o, out var c, out var t);
                if (c >= 0)
                {
                    if (c != o.AClip || t < o.AClipTime - 0.05f || o.AStopped) { o.AClip = c; mask |= M_Anim; }
                    o.AClipTime = t; o.ALastTime = t; o.AStopped = false;
                }
                else if (o.AClip >= 0 && o.AClipTime != float.MaxValue && !o.AStopped)
                {
                    // it is not playing any more: a loop (or a clip stopped half way – the fan whose cable was cut) stays
                    // where it is on every machine, a clip that ran to its end shows its last frame
                    if (StoppedHalfway(o, o.AClip, o.ALastTime)) o.AStopped = true; else o.AClipTime = float.MaxValue;
                    mask |= M_Anim;
                }
            }
            if (U.Alive(o.Animator) && o.Animator.isActiveAndEnabled)
            {
                try
                {
                    var s = o.Animator.GetCurrentAnimatorStateInfo(0);
                    if (s.fullPathHash != o.AAnimState) { o.AAnimState = s.fullPathHash; o.AAnimTime = s.normalizedTime; mask |= M_Animator; }
                }
                catch { }
            }
            if (readStates && o.States != null)
            {
                for (int i = 0; i < o.States.Count; i++)
                {
                    var b = o.States[i];
                    if (!U.Alive(b.Comp)) continue;
                    int v; try { v = b.Read(); } catch { continue; }
                    if (!StateBinding.Equal(b.Kind, v, o.AStates[i])) { o.AStates[i] = v; mask |= M_States; o.StatesDirty |= 1UL << Math.Min(i, 63); }
                }
            }
            if (readStates && o.TrackTag)
            {
                var tag = ReadTag(o);
                if (tag != o.ATag) { o.ATag = tag; mask |= M_Tag; }
            }
            return mask;
        }

        static byte DiffStates(NetObj o)
        {
            byte mask = 0;
            if (o.States == null) return 0;
            for (int i = 0; i < o.States.Count; i++)
            {
                var b = o.States[i];
                if (!U.Alive(b.Comp)) continue;
                int v; try { v = b.Read(); } catch { continue; }
                if (!StateBinding.Equal(b.Kind, v, o.AStates[i])) { o.AStates[i] = v; mask |= M_States; o.StatesDirty |= 1UL << Math.Min(i, 63); }
            }
            return mask;
        }

        /// <summary>The clip stopped before its end, or it is a loop (it never ends by itself).</summary>
        static bool StoppedHalfway(NetObj o, int clip, float lastTime)
        {
            try
            {
                var st = o.Anim[o.Clips[clip]];
                if (st == null) return false;
                var wm = st.wrapMode;
                if (wm == WrapMode.Default) wm = o.Anim.wrapMode;
                if (wm == WrapMode.Loop || wm == WrapMode.PingPong) return true;
                return lastTime < st.length - 0.1f;
            }
            catch { return false; }
        }

        static byte FullMask(NetObj o)
        {
            if (o.StateOnly) { if (o.States != null) o.StatesDirty = ulong.MaxValue; return o.States != null ? M_States : (byte)0; }
            byte m = M_Active | M_Enabled | M_Owner;
            if (o.TrackPose && !o.IsAI) m |= M_Pose;
            if (o.AClip >= 0) m |= M_Anim;
            if (U.Alive(o.Animator)) m |= M_Animator;
            if (o.States != null) { m |= M_States; o.StatesDirty = ulong.MaxValue; }
            if (o.TrackTag && !string.IsNullOrEmpty(o.ATag)) m |= M_Tag;
            return m;
        }

        static NetWriter BeginDelta(byte flags = 0) { var w = new NetWriter(); w.Reset(Msg.WorldDelta); w.UShort(0); w.Byte(flags); return w; }

        /// <summary>A WorldDelta that the receiver applies completely (corrections after a rejected proposal / claim).</summary>
        static NetWriter ForcedDelta(NetObj o)
        {
            var w = Session.W(Msg.WorldDelta); w.UShort(1); w.Byte(DF_Forced); WriteObj(w, o, FullMask(o));
            return w;
        }

        static void FlushDelta(NetWriter w, int count)
        {
            if (count == 0) return;
            w.Buf[1] = (byte)count; w.Buf[2] = (byte)(count >> 8);
            Session.Broadcast(w, true);
        }

        public static void WriteObj(NetWriter w, NetObj o, byte mask)
        {
            w.UInt(o.Id); w.Byte(mask);
            if ((mask & M_Active) != 0) w.Bool(o.AActive);
            if ((mask & M_Pose) != 0) { w.Vec3(o.APos); w.Quat(o.ARot); }
            if ((mask & M_Enabled) != 0) w.UShort(o.AEnabled);
            if ((mask & M_Anim) != 0)
            {
                w.Byte((byte)Math.Max(0, o.AClip));
                float len = 0;
                try { if (U.Alive(o.Anim) && o.AClip >= 0) len = o.Anim[o.Clips[o.AClip]].length; } catch { }
                w.Float(Math.Min(o.AClipTime, len > 0 ? len : o.AClipTime));
                w.Byte((byte)(o.AStopped ? 2 : o.AClipTime == float.MaxValue ? 1 : 0)); // playing / done / stopped half way
            }
            if ((mask & M_Animator) != 0) { w.Int(o.AAnimState); w.Float(o.AAnimTime); }
            if ((mask & M_Owner) != 0) w.Byte(o.Owner);
            if ((mask & M_States) != 0)
            {
                int n = 0; var pos = w.Len; w.Byte(0);
                for (int i = 0; i < o.States.Count && i < 64; i++)
                    if ((o.StatesDirty & (1UL << i)) != 0) { w.Byte((byte)i); w.Int(o.AStates[i]); n++; }
                w.Buf[pos] = (byte)n;
                o.StatesDirty = 0;
            }
            if ((mask & M_Tag) != 0) w.Str(o.ATag ?? "");
        }

        /// <summary>Reads one object update. Returns the object (or null when unknown – the data is skipped).</summary>
        static NetObj ReadObj(NetReader r, out byte mask, out ObjUpdate u)
        {
            u = default;
            var id = r.UInt(); mask = r.Byte();
            u.Mask = mask;
            if ((mask & M_Active) != 0) u.Active = r.Bool();
            if ((mask & M_Pose) != 0) { u.Pos = r.Vec3(); u.Rot = r.Quat(); }
            if ((mask & M_Enabled) != 0) u.Enabled = r.UShort();
            if ((mask & M_Anim) != 0) { u.Clip = r.Byte(); u.ClipTime = r.Float(); var am = r.Byte(); u.ClipDone = am == 1; u.ClipStopped = am == 2; }
            if ((mask & M_Animator) != 0) { u.AnimState = r.Int(); u.AnimTime = r.Float(); }
            if ((mask & M_Owner) != 0) u.Owner = r.Byte();
            if ((mask & M_States) != 0)
            {
                int n = r.Byte();
                u.StateIdx = new byte[n]; u.StateVal = new int[n];
                for (int i = 0; i < n; i++) { u.StateIdx[i] = r.Byte(); u.StateVal[i] = r.Int(); }
            }
            if ((mask & M_Tag) != 0) u.Tag = r.Str();
            Objs.TryGetValue(id, out var o);
            return o;
        }

        internal struct ObjUpdate
        {
            public byte Mask; public bool Active; public Vector3 Pos; public Quaternion Rot; public ushort Enabled;
            public byte Clip; public float ClipTime; public bool ClipDone, ClipStopped; public int AnimState; public float AnimTime; public byte Owner;
            public byte[] StateIdx; public int[] StateVal;
            public string Tag;
        }

        /// <summary>Applies an update to the scene and to the authoritative cache.</summary>
        static void Apply(NetObj o, in ObjUpdate u0, bool authoritative, bool smoothPose)
        {
            if (!U.Alive(o.Go)) return;
            var u = u0;
            if (o.StateOnly) u.Mask &= M_States; // a script on the player: only its fields
            bool wasActive = o.Go.activeSelf;
            Applying = true;
            try
            {
                if ((u.Mask & M_Owner) != 0 && authoritative) SetOwner(o, u.Owner);
                if ((u.Mask & M_Active) != 0) { if (o.Go.activeSelf != u.Active) o.Go.SetActive(u.Active); o.AActive = u.Active; }
                if ((u.Mask & M_Pose) != 0)
                {
                    if (!o.TrackPose && U.Alive(o.Rb))
                    {
                        // released on the host (the car's bumper): a loose body from now on – a client may push it too
                        o.TrackPose = true; o.OrigKinematic = false; o.IsItem = true;
                        Log.Debug("Released on the host, a loose body now: " + o.Path);
                    }
                    o.APos = u.Pos; o.ARot = u.Rot;
                    if (Session.IsClient && PhysBody(o) && o.Owner != Session.LocalId)
                    {
                        // the body only follows the host here – kinematic, moved by interpolation (no local physics)
                        if (!o.Rb.isKinematic) o.Rb.isKinematic = true;
                        o.RemoteDrivenUntil = Time.unscaledTime + 0.4f; // its sounds come from the host
                    }
                    if (smoothPose && (o.Tr.position - u.Pos).sqrMagnitude < 9f) { o.TPos = u.Pos; o.TRot = u.Rot; o.HasTarget = true; }
                    else { o.Tr.position = u.Pos; o.Tr.rotation = u.Rot; o.HasTarget = false; }
                }
                if ((u.Mask & M_Enabled) != 0) { WriteEnabled(o, u.Enabled); o.AEnabled = u.Enabled; }
                if ((u.Mask & M_Anim) != 0) ApplyAnim(o, u.Clip, u.ClipTime, u.ClipDone, u.ClipStopped);
                if ((u.Mask & M_Animator) != 0 && U.Alive(o.Animator))
                {
                    try { if (o.Animator.GetCurrentAnimatorStateInfo(0).fullPathHash != u.AnimState) o.Animator.Play(u.AnimState, 0, u.AnimTime % 1f); } catch { }
                    o.AAnimState = u.AnimState; o.LAnimState = u.AnimState; o.AAnimTime = u.AnimTime;
                }
                if ((u.Mask & M_States) != 0 && o.States != null)
                {
                    for (int i = 0; i < u.StateIdx.Length; i++)
                    {
                        int idx = u.StateIdx[i]; if (idx >= o.States.Count) continue;
                        var b = o.States[idx];
                        try { if (U.Alive(b.Comp) && !StateBinding.Equal(b.Kind, b.Read(), u.StateVal[i])) b.Write(u.StateVal[i]); } catch { }
                        o.AStates[idx] = u.StateVal[i];
                    }
                }
                // after the states: OpenCloseDoors / OpenCloseLoda1 decide "open or close" from the tag
                if ((u.Mask & M_Tag) != 0 && o.TrackTag) { WriteTag(o, u.Tag); o.ATag = u.Tag; }
                o.DivergeSince = 0;
            }
            catch (Exception e) { Log.Debug($"Apply {o.Path}: {e.Message}"); }
            finally { Applying = false; }
            // what the game would have done itself together with this change (Granny's mother falling when the book is placed…)
            if ((u.Mask & M_Active) != 0 && u.Active && !wasActive) Reactions.OnActivated(o);
        }

        static void ApplyAnim(NetObj o, int clip, float time, bool done, bool stopped = false)
        {
            o.AClip = clip; o.AClipTime = done ? float.MaxValue : time; o.AStopped = stopped;
            if (!U.Alive(o.Anim) || o.Clips == null || clip >= o.Clips.Length) return;
            var name = o.Clips[clip];
            try
            {
                var st = o.Anim[name];
                if (st == null) return;
                if (stopped)
                {
                    // stopped where it is (the pose stays as last sampled)
                    if (o.Anim.IsPlaying(name)) o.Anim.Stop(name);
                    o.LClip = clip; o.LPlaying = false;
                    return;
                }
                if (done)
                {
                    // final pose of the clip (e.g. door open)
                    o.Anim.Play(name);
                    st.time = st.length;
                    o.Anim.Sample();
                }
                else if (!o.Anim.IsPlaying(name) || Math.Abs(st.time - time) > 0.35f)
                {
                    if (o.HostOnly) o.Anim.CrossFade(name, 0.15f); else o.Anim.Play(name);
                    st.time = time;
                }
                o.LClip = clip; o.LTime = done ? st.length : time;
            }
            catch (Exception e) { Log.Debug("ApplyAnim: " + e.Message); }
        }

        static void SetOwner(NetObj o, byte owner)
        {
            if (o.Owner == owner) return;
            if (Cfg.DebugLog.Value) Log.Debug($"owner {o.Path}: {(o.Owner == 255 ? "-" : Session.NameOf(o.Owner))} -> {(owner == 255 ? "-" : Session.NameOf(owner))}");
            if (owner == 255) o.ReleasedAt = Time.unscaledTime;
            o.Owner = owner;
            o.OwnerIdleSince = 0; o.PendingClaim = false;
            bool remote = owner != 255 && owner != Session.LocalId;
            // someone else's item: disable all colliders (children too) so it cannot be taken out of their hands
            if (remote != o.CollidersSuppressed)
            {
                o.CollidersSuppressed = remote;
                if (o.AllCols == null)
                {
                    try { o.AllCols = o.Go.GetComponentsInChildren(Il2CppType.Of<Collider>(), true).Select(c => c.TryCast<Collider>()).Where(c => c != null).ToArray(); }
                    catch { o.AllCols = o.Cols; }
                    o.AllColsWas = new bool[o.AllCols.Length];
                }
                for (int i = 0; i < o.AllCols.Length; i++)
                {
                    var c = o.AllCols[i]; if (!U.Alive(c)) continue;
                    if (remote) { o.AllColsWas[i] = c.enabled; c.enabled = false; }
                    else c.enabled = o.AllColsWas[i];
                }
                for (int b = 0; b < o.Cols.Length; b++)
                    if (!remote && U.Alive(o.Cols[b])) o.Cols[b].enabled = (o.AEnabled & (1 << b)) != 0;
            }
            if (U.Alive(o.Rb))
            {
                if (owner == Session.LocalId) o.Rb.isKinematic = o.OrigKinematic;
                else if (remote) o.Rb.isKinematic = true;
                else if (Session.IsClient && PhysBody(o)) o.Rb.isKinematic = true; // back to following the host
                else o.Rb.isKinematic = o.OrigKinematic;
            }
        }

        // ---------- CLIENT ----------
        static readonly List<(NetObj o, byte mask)> _proposals = new();
        static readonly List<(NetObj o, byte mask)> _farAnims = new();
        static readonly List<NetObj> _farStates = new();
        static float _lastNearProposal = -10f;

        static void ClientTick(bool readStates)
        {
            float now = Time.unscaledTime;
            if (!(Session.Local?.SceneReady ?? false)) return; // first the full world state from the host
            Events.PollTriggers();
            Events.FlushDeferred();
            var me = GameRefs.LocalPos;
            bool alive = !(Session.Local?.Ghost ?? false);
            bool canPropose = now > SuppressProposalsUntil && alive;
            float radius = Cfg.ProposeRadius.Value;
            var playerRoot = GameRefs.PlayerRoot;
            bool pushPatched = Patches.Has("pushRigidbody", "OnControllerColliderHit");
            _proposals.Clear();

            for (int li = 0; li < _list.Count; li++)
            {
                var o = _list[li];
                if (!U.Alive(o.Go)) { ClientObjectDestroyed(o, canPropose, me, radius); _list.RemoveAt(li); li--; continue; }
                if (o.PendingSpawn) continue;

                if (o.Owner == Session.LocalId)
                {
                    OwnerTick(o, now, playerRoot);
                    continue;
                }
                if (o.HostOnly)
                {
                    // fields of host-only AI that this player's own game set (the sauna lever, starting the car) go to the host
                    if (readStates && canPropose && o.States != null && ProposeAiFields(o)) _proposals.Add((o, M_States));
                    continue;
                }

                bool phys = PhysBody(o);
                if (phys && !o.Rb.isKinematic)
                {
                    // only the local GAME makes a following body dynamic (triggerFallFloor releasing the attic boards under
                    // this player…): this client simulates it from now on, so it falls exactly like in single player
                    if (alive && o.Owner == 255 && now > StartupUntil && !GameFlow.InTransition && U.Dist(o.Tr.position, me) <= radius)
                    {
                        o.LocalReleased = true; o.OrigKinematic = false; o.IsItem = true;
                        Claim(o, 1);
                        Log.Debug("Released by the local game (simulated here): " + o.Path);
                        continue;
                    }
                    o.Rb.isKinematic = true; // the host simulates it
                    o.Tr.position = o.APos; o.Tr.rotation = o.ARot;
                }
                if (o.HasTarget) continue; // being moved by the network right now
                if (o.Owner != 255)
                {
                    // held by someone else – if the local game "picked it up" anyway, give it back
                    if (o.IsItem && U.IsUnder(o.Tr, playerRoot)) { Session.Notify(L.T("Another player is holding this item!", "Ten przedmiot trzyma inny gracz!"), false); ForceUnhold(o); }
                    continue;
                }
                if (phys && !pushPatched && alive && !o.OrigKinematic && TouchingPlayer(o)) { Claim(o, 0); continue; } // fallback push detection

                byte mask = LocalDiff(o, readStates);
                if (mask == 0) { o.DivergeSince = 0; continue; }

                // the first seconds after loading: the local game still places random items (startNewDay) – those
                // objects follow the host only, never the other way round
                if (now < StartupUntil && (o.IsPickable || o.IsWorld || o.IsContainer) && (mask & (M_Active | M_Pose)) != 0 && !U.IsUnder(o.Tr, playerRoot))
                { Revert(o); continue; }
                o.LastDiffMask = mask;
                // a following body moved by something local (a drawer it lies in, a script): the host's pose wins,
                // its own updates bring the body along – no proposals, a late correction only
                if (phys && !U.IsUnder(o.Tr, playerRoot))
                {
                    mask &= unchecked((byte)~M_Pose);
                    if (mask == 0)
                    {
                        if (o.DivergeSince == 0) o.DivergeSince = now;
                        else if (now - o.DivergeSince > 2f) Revert(o);
                        continue;
                    }
                }
                bool near = DistTo(o, me) <= radius;
                // an item used here sets a fact in a script far away (the battery in the car → checkTheCar.batteryOK on
                // the GameController): a bool that became true is proposed together with this player's action
                if (canPropose && !near && (mask & ~(M_States | M_Tag)) == 0 && (mask & M_States) != 0 && ScriptOnly(o) && HasNewTrue(o))
                {
                    _farStates.Add(o);
                    continue;
                }
                // an animation far away that started with this player's action here (the painting lever moves the
                // bookshelf 16 m away): proposed together with that action (after the loop)
                if (canPropose && !near && (mask & ~(M_Anim | M_Animator | M_Tag)) == 0 && (mask & (M_Anim | M_Animator)) != 0
                    && ((mask & M_Animator) != 0 || o.LClip != o.AClip)) // a new clip, not a loop starting again
                {
                    _farAnims.Add((o, mask));
                    continue;
                }
                if (canPropose && near)
                {
                    // object taken / hidden by this player → take ownership
                    if ((o.IsItem || o.IsSpawned) && !o.PendingClaim && (U.IsUnder(o.Tr, playerRoot) || ((mask & M_Active) != 0 && !o.Go.activeSelf)))
                    {
                        Claim(o, 0);
                        continue;
                    }
                    CommitLocal(o, mask);
                    o.GraceUntil = now + 1.5f; o.GraceMask |= mask;
                    _proposals.Add((o, mask));
                }
                else
                {
                    if (o.DivergeSince == 0) o.DivergeSince = now;
                    else if (now - o.DivergeSince > 0.75f) Revert(o);
                }
            }

            if (_farAnims.Count > 0)
            {
                bool withAction = _proposals.Count > 0 || now - _lastNearProposal < 0.6f;
                foreach (var (o, mask) in _farAnims)
                {
                    if (!withAction) continue; // not this player's doing – the host's state comes anyway
                    CommitLocal(o, mask);
                    o.GraceUntil = now + 1.5f; o.GraceMask |= mask;
                    _proposals.Add((o, mask));
                    Log.Debug("Far animation started by this player's action: " + o.Path);
                }
                _farAnims.Clear();
            }
            if (_farStates.Count > 0)
            {
                // a progress fact of the game's own scripts (GameController: the fuel is in the car, a lock is gone) –
                // only bools that became true, from whatever this player's game did (pouring the fuel is not a click)
                foreach (var o in _farStates)
                {
                    if (!CommitNewTrue(o)) continue;
                    o.GraceUntil = now + 1.5f; o.GraceMask |= M_States;
                    _proposals.Add((o, M_States));
                }
                _farStates.Clear();
            }
            if (_proposals.Count > 0) _lastNearProposal = now;
            if (canPropose) ProjectileClaims();
            ScanClientSpawns(me, radius, canPropose);
            if (_proposals.Count > 0)
            {
                var w = Session.W(Msg.Proposal);
                w.UShort((ushort)_proposals.Count);
                foreach (var (o, m) in _proposals) WriteObj(w, o, m);
                Session.SendToHost(w, true);
            }
        }

        /// <summary>Client: take over an object (optimistically) – from now on this client simulates it and streams its pose.</summary>
        /// <param name="reason">0 = taken / pushed by the player, 1 = released by the local game (falls under this player)</param>
        static void Claim(NetObj o, byte reason)
        {
            float now = Time.unscaledTime;
            if (o.PendingClaim && now - o.LastClaim < 1f) return;
            o.PendingClaim = true; o.LastClaim = now;
            var cw = Session.W(Msg.OwnClaim); cw.UInt(o.Id); cw.Byte(reason); Session.SendToHost(cw, true);
            o.Owner = Session.LocalId; // optimistic – the host's older updates are ignored until it answers
            o.OwnerIdleSince = 0; o.HasTarget = false; o.DivergeSince = 0;
            if (U.Alive(o.Rb) && reason != 1) o.Rb.isKinematic = o.OrigKinematic; // pushed / hit: simulated here at once
            if (Cfg.DebugLog.Value) Log.Debug($"claim {o.Path} ({(reason == 0 ? "player" : "local physics")})");
        }

        /// <summary>
        /// Client (Harmony prefix of pushRigidbody.OnControllerColliderHit): the local player walks into a body that the
        /// host simulates. It is taken over before the game's push code runs, so pushing works like in single player.
        /// </summary>
        public static void OnLocalPush(ControllerColliderHit hit)
        {
            if (!Session.IsClient || !Registered || hit == null || (Session.Local?.Ghost ?? false)) return;
            if (!(Session.Local?.SceneReady ?? false) || Time.unscaledTime < StartupUntil - 6f) return;
            Collider c; try { c = hit.collider; } catch { return; }
            if (!U.Alive(c)) return;
            var rb = c.attachedRigidbody;
            if (!U.Alive(rb)) return;
            if (!_byRb.TryGetValue(rb.Pointer, out var o) || !U.Alive(o.Go) || !PhysBody(o)) return;
            if (o.Owner != 255 || o.OrigKinematic || o.PendingSpawn) return;   // someone else's / not a physics prop on the host
            try { if (hit.moveDirection.y < -0.3f) return; } catch { return; }  // standing on it
            Claim(o, 0);
        }
        static readonly Dictionary<IntPtr, NetObj> _byRb = new();

        /// <summary>
        /// Client: a fast body this player's game simulates (an arrow / dart just shot, a thrown item) takes the prop in
        /// front of it before it gets there – on a client the props are kinematic copies of the host's, so the arrow
        /// stopped at them instead of knocking them over like in the host's game.
        /// </summary>
        /// <summary>
        /// The bodies lying on / in a body an arrow takes over (the screwdriver in the can): taken over with it, else the
        /// can flew away and the screwdriver hung in the air as the host's kinematic copy.
        /// </summary>
        static void ClaimResting(NetObj t)
        {
            try
            {
                if (t.Cols.Length == 0 || !U.Alive(t.Cols[0])) return;
                var tb = t.Cols[0].bounds; Vector3 mn = tb.min - Vector3.one * 0.2f, mx = tb.max + Vector3.one * 0.2f;
                foreach (var x in _list)
                {
                    if (x == t || !PhysBody(x) || x.Owner != 255 || x.OrigKinematic || x.PendingSpawn || x.PendingClaim) continue;
                    if (x.Cols.Length == 0 || !U.Alive(x.Cols[0])) continue;
                    var xb = x.Cols[0].bounds; Vector3 a = xb.min, z = xb.max; // Bounds.Intersects is stripped from the game
                    if (a.x > mx.x || z.x < mn.x || a.y > mx.y || z.y < mn.y || a.z > mx.z || z.z < mn.z) continue;
                    Claim(x, 2);
                    x.HoldUntil = Time.unscaledTime + 3f;
                    if (Cfg.DebugLog.Value) Log.Debug($"{x.Path} lies on {t.Path} – taken over with it");
                }
            }
            catch { }
        }

        static void ProjectileClaims()
        {
            foreach (var o in _list)
            {
                if (o.Owner != Session.LocalId || !U.Alive(o.Rb) || o.Rb.isKinematic) continue;
                Vector3 v; try { v = o.Rb.linearVelocity; } catch { continue; }
                float speed = v.magnitude;
                if (speed < 4f) continue;
                try
                {
                    if (!Physics.Raycast(o.Tr.position, v / speed, out var hit, speed * 0.12f + 0.4f, ~0, QueryTriggerInteraction.Ignore)) continue;
                    var rb = hit.collider != null ? hit.collider.attachedRigidbody : null;
                    if (!U.Alive(rb) || !_byRb.TryGetValue(rb.Pointer, out var t) || t == o || !PhysBody(t)) continue;
                    if (t.Owner != 255 || t.OrigKinematic || t.PendingSpawn || t.PendingClaim) continue;
                    Claim(t, 2);
                    t.HoldUntil = Time.unscaledTime + 3f; // it lies still until the arrow gets there – not handed back at once
                    ClaimResting(t);
                    if (Cfg.DebugLog.Value) Log.Debug($"{o.Path} hits {t.Path} – taken over");
                }
                catch { }
            }
        }

        /// <summary>Fallback when the push hook is missing: the player's capsule touches the body while walking into it.</summary>
        static bool TouchingPlayer(NetObj o)
        {
            var cc = GameRefs.PlayerCC;
            if (!U.Alive(cc) || o.Cols.Length == 0 || !U.Alive(o.Cols[0])) return false;
            try
            {
                var v = cc.velocity; v.y = 0;
                if (v.sqrMagnitude < 0.25f) return false;
                var pb = cc.bounds; pb.Expand(0.3f);
                var ob = o.Cols[0].bounds;
                if (!pb.Intersects(ob)) return false;
                var dir = ob.center - pb.center; dir.y = 0;
                return Vector3.Dot(dir.normalized, v.normalized) > 0.3f;
            }
            catch { return false; }
        }

        /// <summary>Compares the local state with the authoritative one (without changing the cache).</summary>
        /// <summary>Client: AI fields this player's game changed (only those allowed – [Advanced] ClientAiFields).</summary>
        /// <summary>An object that only carries scripts (the GameController) – not a door, a drawer or anything visible.</summary>
        /// <summary>A camera on the object or on one of its direct children (a camera rig, not a building holding one).</summary>
        static bool CameraRig(Transform t)
        {
            var ct = Il2CppInterop.Runtime.Il2CppType.Of<Camera>();
            if (t.GetComponent(ct) != null) return true;
            for (int i = 0; i < t.childCount; i++) if (t.GetChild(i).GetComponent(ct) != null) return true;
            return false;
        }

        static bool ScriptOnly(NetObj o) => o.Cols.Length == 0 && o.Rends.Length == 0 && !U.Alive(o.Anim) && !U.Alive(o.Animator) && !U.Alive(o.Rb);

        /// <summary>A bool field of the object's scripts became true here (the host still has false).</summary>
        static bool HasNewTrue(NetObj o)
        {
            if (o.States == null) return false;
            for (int i = 0; i < o.States.Count && i < 64; i++)
            {
                var b = o.States[i];
                if (b.Kind != 0 || !U.Alive(b.Comp)) continue;
                try { if (b.Read() != 0 && o.AStates[i] == 0) return true; } catch { }
            }
            return false;
        }

        /// <summary>Only the bools that became true are proposed (the rest of a far script stays the host's).</summary>
        static bool CommitNewTrue(NetObj o)
        {
            bool any = false;
            o.StatesDirty = 0;
            for (int i = 0; i < o.States.Count && i < 64; i++)
            {
                var b = o.States[i];
                if (b.Kind != 0 || !U.Alive(b.Comp)) continue;
                int v; try { v = b.Read(); } catch { continue; }
                if (v == 0 || o.AStates[i] != 0) continue;
                o.AStates[i] = v; o.StatesDirty |= 1UL << i; any = true;
                Log.Debug($"Item use: {b.Name} -> host");
            }
            if (any) { o.LastProposal = Time.unscaledTime; o.DivergeSince = 0; }
            return any;
        }

        /// <summary>
        /// How far <paramref name="p"/> is from the object: from its colliders' boxes – many parts of the house have
        /// their pivot far away (at the house's origin), so the pivot alone made a hatch next to the player "far".
        /// </summary>
        public static float DistTo(NetObj o, Vector3 p)
        {
            float best = U.Dist(o.Tr.position, p);
            try
            {
                if (o.ShapeCols == null)
                {
                    var list = new List<Collider>();
                    foreach (var c in o.Tr.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Collider>(), true))
                    {
                        var col = c?.TryCast<Collider>();
                        if (col != null && !col.isTrigger) list.Add(col);
                        if (list.Count >= 8) break;
                    }
                    o.ShapeCols = list.ToArray();
                }
                bool got = false; Vector3 mn = default, mx = default;
                foreach (var c in o.ShapeCols)
                {
                    if (!U.Alive(c) || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                    var bb = c.bounds; var a = bb.min; var z = bb.max;
                    if (!got) { mn = a; mx = z; got = true; } else { mn = new Vector3(Mathf.Min(mn.x, a.x), Mathf.Min(mn.y, a.y), Mathf.Min(mn.z, a.z)); mx = new Vector3(Mathf.Max(mx.x, z.x), Mathf.Max(mx.y, z.y), Mathf.Max(mx.z, z.z)); }
                }
                if (got) { o.ShapeMin = mn; o.ShapeMax = mx; o.HasShape = true; }
                if (o.HasShape)
                {
                    var q = new Vector3(Mathf.Clamp(p.x, o.ShapeMin.x, o.ShapeMax.x), Mathf.Clamp(p.y, o.ShapeMin.y, o.ShapeMax.y), Mathf.Clamp(p.z, o.ShapeMin.z, o.ShapeMax.z));
                    best = Mathf.Min(best, (q - p).magnitude);
                }
            }
            catch { }
            return best;
        }

        static bool ProposeAiFields(NetObj o)
        {
            bool any = false;
            o.StatesDirty = 0;
            for (int i = 0; i < o.States.Count && i < 64; i++)
            {
                var b = o.States[i];
                if (!b.ClientMay || !U.Alive(b.Comp)) continue;
                int v; try { v = b.Read(); } catch { continue; }
                if (StateBinding.Equal(b.Kind, v, o.AStates[i])) continue;
                o.AStates[i] = v; o.StatesDirty |= 1UL << i; any = true;
                if (Cfg.DebugLog.Value) Log.Debug($"AI field -> host: {b.Name}");
            }
            return any;
        }

        static byte LocalDiff(NetObj o, bool readStates)
        {
            byte mask = 0;
            if (o.StateOnly)
            {
                if (readStates && o.States != null)
                    for (int i = 0; i < o.States.Count; i++)
                        try { if (U.Alive(o.States[i].Comp) && !StateBinding.Equal(o.States[i].Kind, o.States[i].Read(), o.AStates[i])) { mask |= M_States; break; } } catch { }
                return mask;
            }
            if (o.Go.activeSelf != o.AActive) mask |= M_Active;
            if (o.TrackPose && !o.HasTarget && ((o.Tr.position - o.APos).sqrMagnitude > 0.01f || Quaternion.Angle(o.Tr.rotation, o.ARot) > 3f))
            {
                // a dynamic body that only settled a little (physics jitter) is not a player action
                bool settling = U.Alive(o.Rb) && !o.Rb.isKinematic && o.Rb.IsSleeping() && (o.Tr.position - o.APos).sqrMagnitude < 0.0225f;
                if (!settling) mask |= M_Pose;
            }
            if (!o.CollidersSuppressed && !o.LocalEnabled && ReadEnabled(o) != o.AEnabled) mask |= M_Enabled;
            if (U.Alive(o.Anim))
            {
                ReadAnim(o, out var c, out var t);
                if (c >= 0 && (c != o.LClip || t < o.LTime - 0.05f)) mask |= M_Anim;
                if (c >= 0) { o.LClip = c; o.LTime = t; o.LocalClipTime = t; o.LocalClip = c; o.LPlaying = true; o.LLastTime = t; }
                else if (o.LPlaying)
                {
                    o.LPlaying = false;
                    // stopped here half way (the fan whose cable this player cut): the others stop it too
                    if (o.LClip >= 0 && !o.AStopped && StoppedHalfway(o, o.LClip, o.LLastTime)) { mask |= M_Anim; o.LocalStopped = true; }
                }
            }
            if (U.Alive(o.Animator) && o.Animator.isActiveAndEnabled)
            {
                try { var s = o.Animator.GetCurrentAnimatorStateInfo(0).fullPathHash; if (s != o.LAnimState) { o.LAnimState = s; if (s != o.AAnimState) mask |= M_Animator; } } catch { }
            }
            if (readStates && o.States != null)
            {
                for (int i = 0; i < o.States.Count; i++)
                {
                    try { if (U.Alive(o.States[i].Comp) && !StateBinding.Equal(o.States[i].Kind, o.States[i].Read(), o.AStates[i])) { mask |= M_States; break; } } catch { }
                }
            }
            if (readStates && o.TrackTag && ReadTag(o) != o.ATag) mask |= M_Tag;
            return mask;
        }

        /// <summary>Copies local values into the cache (optimistically) before sending a proposal.</summary>
        static void CommitLocal(NetObj o, byte mask)
        {
            if ((mask & M_Active) != 0) o.AActive = o.Go.activeSelf;
            if ((mask & M_Pose) != 0) { o.APos = o.Tr.position; o.ARot = o.Tr.rotation; }
            if ((mask & M_Enabled) != 0) o.AEnabled = ReadEnabled(o);
            if ((mask & M_Anim) != 0)
            {
                if (o.LocalStopped) { o.AStopped = true; o.LocalStopped = false; }
                else { o.AClip = o.LocalClip; o.AClipTime = o.LocalClipTime; o.AStopped = false; }
            }
            if ((mask & M_Animator) != 0) { o.AAnimState = o.LAnimState; o.AAnimTime = 0; }
            if ((mask & M_States) != 0)
            {
                o.StatesDirty = 0;
                for (int i = 0; i < o.States.Count; i++)
                {
                    try { var v = o.States[i].Read(); if (!StateBinding.Equal(o.States[i].Kind, v, o.AStates[i])) { o.AStates[i] = v; o.StatesDirty |= 1UL << Math.Min(i, 63); } } catch { }
                }
            }
            if ((mask & M_Tag) != 0) o.ATag = ReadTag(o);
            o.LastProposal = Time.unscaledTime;
            o.DivergeSince = 0;
        }

        /// <summary>Restores the authoritative state (the local change did not come from the player).</summary>
        static void Revert(NetObj o)
        {
            ushort localBits = ReadEnabled(o);
            var u = new ObjUpdate { Mask = (byte)(M_Active | M_Enabled | (o.TrackPose ? M_Pose : 0) | (o.AClip >= 0 ? M_Anim : 0) | (o.States != null ? M_States : 0) | (o.TrackTag ? M_Tag : 0)), Active = o.AActive, Pos = o.APos, Rot = o.ARot, Enabled = o.AEnabled, Tag = o.ATag };
            if (o.AClip >= 0) { u.Clip = (byte)o.AClip; u.ClipDone = !o.AStopped; u.ClipStopped = o.AStopped; }
            if (o.StateOnly) u.Mask = o.States != null ? M_States : (byte)0;
            if (o.States != null)
            {
                u.StateIdx = new byte[o.States.Count]; u.StateVal = new int[o.States.Count];
                for (int i = 0; i < o.States.Count; i++) { u.StateIdx[i] = (byte)i; u.StateVal[i] = o.AStates[i]; }
            }
            Apply(o, u, false, false);
            // the host never played an animation on it, the local game did (Granny's trap snapped shut only here):
            // back to the clip's first frame
            if (o.AClip < 0 && o.LocalClip >= 0) RewindAnim(o);
            o.GraceUntil = 0; o.GraceMask = 0;
            if (Session.IsClient && PhysBody(o) && o.Owner != Session.LocalId) o.Rb.isKinematic = true;
            else if (U.Alive(o.Rb) && !o.Rb.isKinematic) { o.Rb.linearVelocity = Vector3.zero; o.Rb.angularVelocity = Vector3.zero; o.Rb.Sleep(); }
            o.QuietUntil = Time.unscaledTime + 1f;
            o.Reverts++;
            if (o.Reverts <= 3 || o.Reverts % 20 == 0)
                Log.Debug($"Revert #{o.Reverts} {o.Path} (mask {o.LastDiffMask}{((o.LastDiffMask & M_Enabled) != 0 ? $", enabled {localBits:X4} -> {o.AEnabled:X4}" : "")})");
            if ((o.LastDiffMask & M_Enabled) != 0) CheckEnabledFight(o);
        }

        static void RewindAnim(NetObj o)
        {
            try
            {
                if (U.Alive(o.Anim) && o.Clips != null && o.LocalClip < o.Clips.Length)
                {
                    var name = o.Clips[o.LocalClip];
                    var st = o.Anim[name];
                    // the pose stays as last sampled after Stop
                    if (st != null) { o.Anim.Play(name); st.time = 0f; o.Anim.Sample(); o.Anim.Stop(); }
                }
            }
            catch (Exception e) { Log.Debug("rewind: " + e.Message); }
            o.LocalClip = -1; o.LClip = -1; o.LTime = float.MaxValue;
        }

        /// <summary>
        /// Client: the enabled state was reverted. When it still differs (a component looked up earlier is gone) the
        /// components are looked up again; a clone whose own scripts keep changing it is not compared any more – the
        /// reverts would never end (the host's changes still arrive).
        /// </summary>
        static void CheckEnabledFight(NetObj o)
        {
            if (o.CollidersSuppressed || o.LocalEnabled) return;
            if (ReadEnabled(o) != o.AEnabled)
            {
                try
                {
                    o.Cols = o.Go.GetComponents<Collider>().ToArray().Take(8).ToArray();
                    o.Rends = o.Go.GetComponents<Renderer>().ToArray().Take(4).ToArray();
                    if (!o.IsSpawned) o.Behs = o.Go.GetComponents<NavMeshObstacle>().ToArray().Select(x => (Behaviour)x).Take(3).ToArray();
                    WriteEnabled(o, o.AEnabled);
                }
                catch (Exception e) { Log.Debug("enabled refresh: " + e.Message); }
            }
            var now = ReadEnabled(o);
            if (now == o.AEnabled && ++o.EnabledFights < 6) return;
            if (!o.IsSpawned) { o.AEnabled = now; return; }
            o.LocalEnabled = true;
            Log.Debug($"{o.Path}: the local game keeps its own enabled state ({now:X4}, host {o.AEnabled:X4}) – no longer compared");
        }

        static void OwnerTick(NetObj o, float now, Transform playerRoot)
        {
            // stream the pose of an owned (held/thrown/pushed) object
            if (now - o.LastPoseSend > 1f / 20f)
            {
                o.LastPoseSend = now;
                var w = Session.W(Msg.ItemPose);
                w.UInt(o.Id); w.Vec3(o.Tr.position); w.Quat(o.Tr.rotation); w.Bool(o.Go.activeSelf);
                Session.SendToHost(w, false);
            }
            bool held = U.IsUnder(o.Tr, playerRoot);
            bool resting = !o.Go.activeInHierarchy || !U.Alive(o.Rb) || o.Rb.isKinematic || o.Rb.IsSleeping();
            // a thrown / dropped item is given back a second after it left the hand, even while it still rolls –
            // as long as this player simulated it nobody else could pick it up
            if (held) o.LeftHandAt = 0;
            else if (o.LeftHandAt == 0) o.LeftHandAt = now;
            if (!held && (o.IsPickable || o.IsSpawned) && o.LeftHandAt > 0 && now - o.LeftHandAt > 1f && now >= o.HoldUntil)
            {
                o.LeftHandAt = 0;
                Release(o);
                return;
            }
            if (!held && resting && now >= o.HoldUntil)
            {
                if (o.OwnerIdleSince == 0) o.OwnerIdleSince = now;
                else if (now - o.OwnerIdleSince > 1f) Release(o);
            }
            else o.OwnerIdleSince = 0;
        }

        static void Release(NetObj o)
        {
            o.AActive = o.Go.activeSelf; o.APos = o.Tr.position; o.ARot = o.Tr.rotation; o.AEnabled = ReadEnabled(o);
            // DynBit = the body is a normal physics object now (e.g. attic boards the local game released);
            // a body the game made kinematic again (a board lying on the floor with its collider off) stays kinematic
            if (U.Alive(o.Rb) && !o.HostOnly) o.AEnabled = (ushort)(!o.Rb.isKinematic ? (o.AEnabled | DynBit) : (o.AEnabled & ~DynBit));
            var w = Session.W(Msg.OwnRelease);
            w.UInt(o.Id); w.Bool(o.AActive); w.Vec3(o.APos); w.Quat(o.ARot); w.UShort(o.AEnabled);
            Session.SendToHost(w, true);
            if (Cfg.DebugLog.Value) Log.Debug($"release {o.Path}{(o.LocalReleased ? " (local physics)" : "")}");
            o.LocalReleased = false;
            SetOwner(o, 255);
        }

        /// <summary>The object or one of its parents is moved by an animation (door leaves, drawers…).</summary>
        static bool HasForwardedScript(Transform t)
        {
            try
            {
                foreach (var mb in t.GetComponents<MonoBehaviour>())
                    if (mb != null && Cfg.ForwardSet.Contains(U.Il2CppTypeName(mb))) return true;
            }
            catch { }
            return false;
        }

        static bool AnimatedChain(Transform t)
        {
            for (var x = t; x != null; x = x.parent)
                if (x.GetComponent<Animation>() != null || x.GetComponent<Animator>() != null) return true;
            return false;
        }

        static void SmoothPoses()
        {
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 15f);
            foreach (var o in _list)
            {
                if (!o.HasTarget || !U.Alive(o.Tr)) continue;
                if (Session.IsClient && (o.Owner == Session.LocalId || (PhysBody(o) && !o.Rb.isKinematic))) { o.HasTarget = false; continue; } // simulated here
                o.Tr.position = Vector3.Lerp(o.Tr.position, o.TPos, k);
                o.Tr.rotation = Quaternion.Slerp(o.Tr.rotation, o.TRot, k);
                if ((o.Tr.position - o.TPos).sqrMagnitude < 0.00001f) { o.Tr.position = o.TPos; o.Tr.rotation = o.TRot; o.HasTarget = false; }
            }
        }

        /// <summary>Called every frame (smooth AI and object poses).</summary>
        public static void FrameUpdate()
        {
            if (!Registered || !Session.Active) return;
            SmoothPoses();
            if (Session.IsClient)
            {
                foreach (var o in _list)
                {
                    // AI is driven only by the network on clients
                    if (o.IsAI && U.Alive(o.Agent) && o.Agent.enabled) o.Agent.enabled = false;
                }
            }
        }

        // ======================= AI =======================
        static void SendAiPoses()
        {
            var w = Session.W(Msg.AiPose);
            int n = 0; var pos = w.Len; w.Byte(0);
            foreach (var o in _list)
            {
                if (!o.IsAI || !U.Alive(o.Go) || !o.Go.activeInHierarchy) continue;
                w.UInt(o.Id); w.Vec3(o.Tr.position); w.Quat(o.Tr.rotation);
                if (++n >= 40) break;
            }
            w.Buf[pos] = (byte)n;
            if (n > 0) Session.Broadcast(w, false);
        }

        // ======================= destroyed objects (picked-up items) =======================
        static void HostObjectDestroyed(NetObj o)
        {
            Objs.Remove(o.Id); _trById.Remove(o.Id);
            if (!o.ExpectDestroy) Weapons.PickedUp(o); // the host picked it up itself
            var w = Session.W(Msg.Despawn); w.UInt(o.Id); Session.Broadcast(w, true);
            Log.Debug("Destroyed on host: " + o.Path);
        }

        static void ClientObjectDestroyed(NetObj o, bool canPropose, Vector3 me, float radius)
        {
            Objs.Remove(o.Id); _trById.Remove(o.Id);
            if (o.ExpectDestroy || o.PendingSpawn) return;
            // destroyed by the local game near the local player = picked up by this player
            if (canPropose && U.Dist(o.APos, me) <= radius + 2f)
            {
                var w = Session.W(Msg.DestroyReq); w.UInt(o.Id); Session.SendToHost(w, true);
                Weapons.PickedUp(o);
                Log.Debug("Picked up / destroyed locally: " + o.Path);
            }
        }

        // ======================= spawns (clones: dropped items, Granny's traps, ragdolls…) =======================
        static uint _nextTempId = 0xC0000001;
        static readonly Dictionary<uint, NetObj> _pendingSpawns = new();

        static NetObj MakeSpawnObj(uint id, GameObject go, string prefabName)
        {
            var o = new NetObj { Id = id, Go = go, Tr = go.transform, Path = "spawn:" + prefabName, PrefabName = prefabName, TrackPose = true, IsSpawned = true, Anim = go.GetComponent<Animation>(), Rb = go.GetComponent<Rigidbody>() };
            if (o.Rb != null) o.OrigKinematic = o.Rb.isKinematic;
            if (o.Anim != null) { int c = o.Anim.GetStateCount(); o.Clips = new string[c]; for (int i = 0; i < c; i++) o.Clips[i] = o.Anim.GetStateAtIndex(i)?.name ?? ""; }
            o.Cols = go.GetComponents<Collider>().ToArray().Take(8).ToArray();
            o.Rends = go.GetComponents<Renderer>().ToArray().Take(4).ToArray();
            // script state of the clone too (e.g. whether Granny's bear trap has already snapped shut) – the same
            // prefab gives the same fields in the same order on every machine
            try { o.States = StatesOf(go.transform); } catch (Exception e) { Log.Debug("spawn states: " + e.Message); }
            Snapshot(o);
            o.LClip = o.AClip; o.LTime = o.AClipTime;
            return o;
        }

        static void AddObj(NetObj o)
        {
            Objs[o.Id] = o; _list.Add(o);
            _idByTr[o.Tr.Pointer] = o.Id; _trById[o.Id] = o.Tr;
            if (U.Alive(o.Rb)) _byRb[o.Rb.Pointer] = o;
        }

        /// <summary>Clones that matter only to the AI on the host (Granny's noise targets) – never sent.</summary>
        static bool Unsynced(string baseName) => Cfg.UnsyncedSpawnSet.Contains(baseName);

        static string BaseName(string n) => n.EndsWith("(Clone)", StringComparison.Ordinal) ? n.Substring(0, n.Length - 7).Trim() : n;

        static void WriteSpawn(NetWriter w, NetObj o, byte origin, uint tempId)
        {
            w.UInt(o.Id); w.Str(o.PrefabName); w.Vec3(o.Tr.position); w.Quat(o.Tr.rotation); w.Bool(o.Go.activeSelf);
            w.Byte(o.Owner); w.Byte(origin); w.UInt(tempId);
            var v = Vector3.zero; try { if (U.Alive(o.Rb)) v = o.Rb.linearVelocity; } catch { }
            w.Vec3(v);
            w.Byte((byte)(o.Loaded + 1));
        }

        /// <summary>Host: new root clones created by the host's game (dropped items, Granny's bear trap, ragdolls).</summary>
        static void ScanSpawns()
        {
            if (Time.unscaledTime - _lastSpawnScan < 0.25f) return;
            _lastSpawnScan = Time.unscaledTime;
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (_knownRoots.Contains(root.Pointer)) continue;
                _knownRoots.Add(root.Pointer);
                if (!root.name.EndsWith("(Clone)", StringComparison.Ordinal)) continue;
                if (U.IsUnder(root.transform, GameRefs.PlayerRoot) || root.GetComponent<Camera>() != null) continue;
                if (IsPersonal(root.transform) || Unsynced(BaseName(root.name))) continue;
                if (DuplicateOfClientDrop(root)) continue;
                var o = MakeSpawnObj(_nextSpawnId++, root, BaseName(root.name));
                if (Weapons.IsWeapon(o.PrefabName)) o.Loaded = Weapons.LocalLoaded(o.PrefabName);
                AddObj(o);
                var w = Session.W(Msg.Spawn); WriteSpawn(w, o, 255, 0);
                Session.Broadcast(w, true);
                Log.Debug("Spawn " + root.name + " -> " + o.Id + (o.Loaded >= 0 ? (o.Loaded == 1 ? " loaded" : " empty") : ""));
            }
        }

        /// <summary>Client: a clone created by the local game near the local player (= this player dropped an item).</summary>
        static void ScanClientSpawns(Vector3 me, float radius, bool canPropose)
        {
            if (Time.unscaledTime - _lastSpawnScan < 0.2f) return;
            _lastSpawnScan = Time.unscaledTime;
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            // the held item is dropped by the game at the end of the day (CheckInventory) and when the player
            // becomes a spectator – such clones must reach the host even while proposals are suppressed
            bool dayEnd = GameFlow.InTransition || Time.unscaledTime < AllowLocalSpawnsUntil;
            var roots = new List<GameObject>(scene.GetRootGameObjects());
            if ((canPropose || dayEnd) && _heldBackRoots.Count > 0)
            {
                // clones held back a moment ago (dropped during the catch) – not older than 15 s
                foreach (var kv in _heldBackRoots)
                    if (U.Alive(kv.Value.go) && Time.unscaledTime - kv.Value.at < 15f && kv.Value.go.transform.parent == null)
                    {
                        _knownRoots.Remove(kv.Key); roots.Add(kv.Value.go);
                        Log.Debug("Dropped while caught, sent now: " + kv.Value.go.name);
                    }
                _heldBackRoots.Clear();
            }
            foreach (var root in roots)
            {
                if (!U.Alive(root) || _knownRoots.Contains(root.Pointer)) continue;
                _knownRoots.Add(root.Pointer);
                if (!root.name.EndsWith("(Clone)", StringComparison.Ordinal)) continue;
                if (!(canPropose || dayEnd))
                {
                    if (!root.name.StartsWith("GrannyMP_", StringComparison.Ordinal) && U.Dist(root.transform.position, me) <= radius + 2f)
                        _heldBackRoots[root.Pointer] = (root, Time.unscaledTime);
                    continue;
                }
                if (root.name.StartsWith("GrannyMP_", StringComparison.Ordinal)) continue;
                if (IsPersonal(root.transform) || Unsynced(BaseName(root.name))) continue;
                if (!dayEnd && U.Dist(root.transform.position, me) > radius) continue;
                if (root.GetComponent<Rigidbody>() == null && root.GetComponent<Collider>() == null) continue; // effects / decals
                var o = MakeSpawnObj(_nextTempId++, root, BaseName(root.name));
                o.PendingSpawn = true; o.Owner = Session.LocalId;
                if (Weapons.IsWeapon(o.PrefabName)) o.Loaded = Weapons.LocalLoaded(o.PrefabName);
                _pendingSpawns[o.Id] = o;
                AddObj(o);
                var w = Session.W(Msg.SpawnReq);
                w.UInt(o.Id); w.Str(o.PrefabName); w.Vec3(root.transform.position); w.Quat(root.transform.rotation); w.Bool(root.activeSelf);
                w.Byte((byte)(o.Loaded + 1)); // a dropped weapon: loaded or not
                Session.SendToHost(w, true);
                Log.Debug("Dropped item spawn request: " + root.name + (o.Loaded >= 0 ? (o.Loaded == 1 ? " loaded" : " empty") : ""));
            }
        }

        static Dictionary<string, GameObject> _prefabByName;
        /// <summary>Template for a clone: prefer a prefab asset, else an (inactive) scene object with that name.</summary>
        static GameObject FindPrefab(string name)
        {
            if (_prefabCache == null)
            {
                _prefabCache = new Dictionary<string, GameObject>();
                _prefabByName = new Dictionary<string, GameObject>();
                foreach (var g in U.AllOfType<GameObject>())
                {
                    try
                    {
                        var n = g.name;
                        if (n.EndsWith("(Clone)", StringComparison.Ordinal) || n.StartsWith("GrannyMP_", StringComparison.Ordinal)) continue;
                        // the mod's own spare copies are only the last resort (a real prefab / scene object is the same
                        // as on the host – a copy taken from a placed object can differ, e.g. a closed bear trap)
                        if (U.Alive(_templateRoot) && g.transform.root == _templateRoot.transform) continue;
                        if (!g.scene.IsValid()) { if (g.transform.parent == null && !_prefabCache.ContainsKey(n)) _prefabCache[n] = g; }
                        else if (!_prefabByName.TryGetValue(n, out var cur) || (cur.activeInHierarchy && !g.activeInHierarchy)) _prefabByName[n] = g;
                    }
                    catch { }
                }
            }
            if (_prefabCache.TryGetValue(name, out var p) && U.Alive(p)) return p;
            if (_prefabByName.TryGetValue(name, out p) && U.Alive(p)) return p;
            if (_templates.TryGetValue(name, out p) && U.Alive(p)) return p;
            _prefabCache = null; // assets may have been loaded later – rebuild next time
            return null;
        }

        // copies of the pickable items taken at scene start: a clone of an item (dropped crossbow, syringe…) can be
        // made even when every original of it has already been picked up / destroyed on this machine
        static readonly Dictionary<string, GameObject> _templates = new();
        static GameObject _templateRoot;

        static void MakeTemplates()
        {
            _templates.Clear();
            if (U.Alive(_templateRoot)) UnityEngine.Object.Destroy(_templateRoot);
            _templateRoot = new GameObject("GrannyMP_Templates");
            _templateRoot.SetActive(false);
            _knownRoots.Add(_templateRoot.Pointer);
            int n = 0;
            foreach (var o in _list)
            {
                if (!o.IsPickable || !U.Alive(o.Go)) continue;
                var name = o.Go.name;
                if (_templates.ContainsKey(name)) continue;
                try
                {
                    // under an inactive parent the copy never wakes up (no scripts run, nothing is registered)
                    var copy = UnityEngine.Object.Instantiate((UnityEngine.Object)o.Go, _templateRoot.transform)?.TryCast<GameObject>();
                    if (copy == null) continue;
                    copy.name = name;
                    _templates[name] = copy; n++;
                }
                catch { }
            }
            Log.Debug($"Spawn templates: {n}");
        }

        static GameObject InstantiateByName(string name, Vector3 pos, Quaternion rot, bool active)
        {
            var prefab = FindPrefab(name);
            if (prefab == null) { Log.Warn("No template for spawned object: " + name); return null; }
            var go = UnityEngine.Object.Instantiate((UnityEngine.Object)prefab, pos, rot)?.TryCast<GameObject>();
            if (go == null) return null;
            if (go.transform.parent != null) go.transform.SetParent(null, true);
            go.name = name + "(Clone)";
            // a copy of somebody else's object never films or listens for this player (e.g. a dead player's falling camera)
            try
            {
                foreach (var c in go.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Camera>(), true)) { var cam = c?.TryCast<Camera>(); if (cam != null) cam.enabled = false; }
                foreach (var c in go.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<AudioListener>(), true)) { var al = c?.TryCast<AudioListener>(); if (al != null) al.enabled = false; }
            }
            catch (Exception e) { Log.Debug("spawn cameras: " + e.Message); }
            go.SetActive(active);
            _knownRoots.Add(go.Pointer);
            return go;
        }

        // ======================= messages =======================
        public static void RegisterHandlers()
        {
            Session.On(Msg.WorldDelta, (p, r) => { if (Session.IsClient) ReadDelta(r, false); });
            Session.On(Msg.WorldFull, (p, r) => { if (Session.IsClient) { ReadDelta(r, true); Log.Info("Received full world state"); GameFlow.OnWorldFullReceived(); } });
            Session.On(Msg.DestroyReq, HandleDestroyReq);
            Session.On(Msg.SpawnReq, HandleSpawnReq);
            Session.On(Msg.Proposal, HandleProposal);
            Session.On(Msg.OwnClaim, HandleClaim);
            Session.On(Msg.OwnResult, HandleClaimResult);
            Session.On(Msg.OwnRelease, HandleRelease);
            Session.On(Msg.ItemPose, HandleItemPose);
            Session.On(Msg.AiPose, HandleAiPose);
            Session.On(Msg.Spawn, HandleSpawn);
            Session.On(Msg.Despawn, (p, r) =>
            {
                if (!Session.IsClient) return;
                var id = r.UInt();
                if (Objs.TryGetValue(id, out var o)) { o.ExpectDestroy = true; if (U.Alive(o.Go)) UnityEngine.Object.Destroy(o.Go); Objs.Remove(id); _list.Remove(o); _trById.Remove(id); }
            });
        }

        static void ReadDelta(NetReader r, bool full)
        {
            if (!Registered) return;
            int n = r.UShort();
            byte flags = r.Byte();
            bool forced = full || (flags & DF_Forced) != 0;
            float now = Time.unscaledTime;
            for (int i = 0; i < n; i++)
            {
                var o = ReadObj(r, out _, out var u);
                if (o == null) continue;
                // taken over by this client (also while the host has not answered the claim yet): pose, activity and
                // owner are decided here – an older host update must not take it back (it threw the falling boards up again)
                if (o.Owner == Session.LocalId || o.PendingClaim)
                {
                    bool takenAway = !o.PendingClaim && (u.Mask & M_Owner) != 0 && u.Owner != Session.LocalId;
                    if (!takenAway) u.Mask = (byte)(u.Mask & ~(M_Pose | M_Active | M_Enabled | M_Owner));
                }
                // an update sent before the host processed this client's release – it would hand the object back
                else if ((u.Mask & M_Owner) != 0 && u.Owner == Session.LocalId && now - o.ReleasedAt < 2f) u.Mask = (byte)(u.Mask & ~(M_Owner | M_Pose));
                // a following body the local game has just released: ClientTick takes it over, no pulling it back first
                else if (PhysBody(o) && o.Owner == 255 && !o.Rb.isKinematic) u.Mask = (byte)(u.Mask & ~M_Pose);
                if (!forced && !o.HostOnly) DropRepeats(o, ref u, now); // AI keeps every correction (it is never checked locally)
                if (u.Mask == 0) continue;
                Apply(o, u, true, !full && (o.TrackPose || o.IsItem));
            }
        }

        /// <summary>
        /// Client: keep only real changes. A value equal to the last agreed one is not written again – the host repeats
        /// every object every few seconds, and re-writing an old value over a fresh local change (a door the player has
        /// just opened, not yet proposed) left the door half-synced: it could only be opened, never closed.
        /// Fields this client has just proposed ignore the host's older values for a moment.
        /// </summary>
        static void DropRepeats(NetObj o, ref ObjUpdate u, float now)
        {
            byte m = u.Mask;
            if (o.GraceMask != 0)
            {
                if (now < o.GraceUntil) m = (byte)(m & ~o.GraceMask);
                else o.GraceMask = 0;
            }
            if ((m & M_Active) != 0 && u.Active == o.AActive) m = (byte)(m & ~M_Active);
            if ((m & M_Pose) != 0 && (u.Pos - o.APos).sqrMagnitude < 1e-6f && Quaternion.Angle(u.Rot, o.ARot) < 0.05f) m = (byte)(m & ~M_Pose);
            if ((m & M_Enabled) != 0 && u.Enabled == o.AEnabled) m = (byte)(m & ~M_Enabled);
            if ((m & M_Anim) != 0 && u.Clip == o.AClip && ((u.ClipDone && o.AClipTime == float.MaxValue) || (u.ClipStopped && o.AStopped))) m = (byte)(m & ~M_Anim);
            if ((m & M_Animator) != 0 && u.AnimState == o.AAnimState) m = (byte)(m & ~M_Animator);
            if ((m & M_Owner) != 0 && u.Owner == o.Owner) m = (byte)(m & ~M_Owner);
            if ((m & M_Tag) != 0 && u.Tag == o.ATag) m = (byte)(m & ~M_Tag);
            if ((m & M_States) != 0 && o.States != null && o.AStates != null && u.StateIdx != null)
            {
                int keep = 0;
                for (int i = 0; i < u.StateIdx.Length; i++)
                {
                    int idx = u.StateIdx[i];
                    if (idx < o.AStates.Length && StateBinding.Equal(o.States[idx].Kind, u.StateVal[i], o.AStates[idx])) continue;
                    u.StateIdx[keep] = u.StateIdx[i]; u.StateVal[keep] = u.StateVal[i]; keep++;
                }
                if (keep == 0) m = (byte)(m & ~M_States);
                else if (keep < u.StateIdx.Length) { Array.Resize(ref u.StateIdx, keep); Array.Resize(ref u.StateVal, keep); }
            }
            u.Mask = m;
        }

        public static void SendFullTo(byte pid)
        {
            if (!Session.IsHost || !Registered) return;
            var w = new NetWriter(); w.Reset(Msg.WorldFull); w.UShort(0); w.Byte(DF_Forced);
            int count = 0;
            foreach (var o in _list)
            {
                if (!U.Alive(o.Go) || o.IsSpawned) continue;
                WriteObj(w, o, FullMask(o));
                count++;
            }
            w.Buf[1] = (byte)count; w.Buf[2] = (byte)(count >> 8);
            Session.SendTo(pid, w, true);
            // spawned clones
            foreach (var o in _list.Where(x => x.IsSpawned && U.Alive(x.Go)))
            {
                var sw = Session.W(Msg.Spawn); WriteSpawn(sw, o, 255, 0);
                Session.SendTo(pid, sw, true);
            }
            // objects already destroyed on the host (picked up before this player joined)
            var dead = _registeredIds.Where(id => !Objs.ContainsKey(id) && (id & SpawnBit) == 0).ToList();
            foreach (var id in dead) { var dw = Session.W(Msg.Despawn); dw.UInt(id); Session.SendTo(pid, dw, true); }
            TagSync.SendAllTo(pid);
            Log.Info($"Sent full world state ({count} objects, {w.Len / 1024} KB, {dead.Count} removed) to {Session.NameOf(pid)}");
        }

        static readonly Dictionary<byte, float> _nearActionAt = new();

        static bool PlayerNear(NetPlayer p, Vector3 pos, float extra)
        {
            if (!PlayerSync.TryGetPos(p.Id, out var pp)) return true;
            return U.Dist(pp, pos) <= Cfg.ProposeRadius.Value + extra;
        }

        static bool PlayerNear(NetPlayer p, NetObj o, float extra)
        {
            if (!PlayerSync.TryGetPos(p.Id, out var pp)) return true;
            return DistTo(o, pp) <= Cfg.ProposeRadius.Value + extra;
        }

        static void HandleProposal(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null || !Registered) return;
            int n = r.UShort();
            var rejected = new List<NetObj>();
            var batch = new List<(NetObj o, ObjUpdate u)>();
            for (int i = 0; i < n; i++)
            {
                var o0 = ReadObj(r, out _, out var u0);
                if (o0 != null) batch.Add((o0, u0));
            }
            // a lever / button near the player that starts an animation far away (the bookshelf behind the painting
            // lever is 16 m from it): far animation-only changes are accepted with a change near that player
            float now = Time.unscaledTime;
            if (batch.Exists(b => !b.o.HostOnly && (b.u.Mask & ~(M_States | M_Tag)) != 0 && PlayerNear(from, b.o, 4f))) _nearActionAt[from.Id] = now;
            bool action = _nearActionAt.TryGetValue(from.Id, out var at) && now - at < 1.5f;
            foreach (var (o, u1) in batch)
            {
                var u = u1;
                u.Mask = (byte)(u.Mask & ~M_Owner);
                bool farAnim = action && (u.Mask & ~(M_Anim | M_Animator | M_Tag)) == 0 && PlayerNear(from, o, 20f);
                // a fact set far away by an item used here (only bools that became true – see the client side)
                bool farState = (action || ScriptOnly(o)) && !o.HostOnly && u.Mask == M_States && o.States != null && u.StateIdx != null
                                && Array.TrueForAll(u.StateIdx, i => i < o.States.Count && o.States[i].Kind == 0) && Array.TrueForAll(u.StateVal, v => v != 0);
                // host-only AI: only the fields a client's own game may set (the sauna lever, starting the car)
                bool aiFields = o.HostOnly && u.Mask == M_States && o.States != null && u.StateIdx != null
                                && Array.TrueForAll(u.StateIdx, i => i < o.States.Count && o.States[i].ClientMay);
                bool ok = (!o.HostOnly || aiFields) && (o.Owner == 255 || o.Owner == from.Id) && !from.Ghost && (aiFields || o.StateOnly || farAnim || farState || PlayerNear(from, o, 4f));
                if (aiFields && Cfg.DebugLog.Value) Log.Debug($"AI fields from {from.Name}: {string.Join(", ", u.StateIdx.Select(i => o.States[i].Name))}");
                if (!ok) { rejected.Add(o); continue; }
                // apply on the host – HostTick detects the change and broadcasts it to everybody
                Applying = true;
                try { ApplyAsHost(o, u); } finally { Applying = false; }
            }
            if (rejected.Count > 0)
            {
                var w = Session.W(Msg.WorldDelta); w.UShort((ushort)rejected.Count); w.Byte(DF_Forced);
                foreach (var o in rejected) WriteObj(w, o, FullMask(o));
                Session.SendTo(from.Id, w, true);
                Log.Debug($"Rejected {rejected.Count} changes from {from.Name}");
            }
        }

        /// <summary>The host applies a client's change to the scene WITHOUT updating the cache (so HostTick broadcasts it).</summary>
        static void ApplyAsHost(NetObj o, ObjUpdate u)
        {
            if (!U.Alive(o.Go)) return;
            if (o.StateOnly) u.Mask &= M_States;
            bool wasActive = o.Go.activeSelf;
            try
            {
                if ((u.Mask & M_Active) != 0 && o.Go.activeSelf != u.Active) o.Go.SetActive(u.Active);
                if ((u.Mask & M_Pose) != 0) { o.Tr.position = u.Pos; o.Tr.rotation = u.Rot; if (U.Alive(o.Rb) && !o.Rb.isKinematic) { o.Rb.linearVelocity = Vector3.zero; o.Rb.angularVelocity = Vector3.zero; } }
                if ((u.Mask & M_Enabled) != 0) WriteEnabled(o, u.Enabled);
                if ((u.Mask & M_Anim) != 0 && U.Alive(o.Anim) && o.Clips != null && u.Clip < o.Clips.Length)
                {
                    var name = o.Clips[u.Clip];
                    if (u.ClipStopped) { if (o.Anim.IsPlaying(name)) o.Anim.Stop(name); }
                    else if (u.ClipDone) { o.Anim.Play(name); var st = o.Anim[name]; if (st != null) { st.time = st.length; o.Anim.Sample(); } }
                    else { o.Anim.Play(name); var st = o.Anim[name]; if (st != null) st.time = u.ClipTime; }
                }
                if ((u.Mask & M_Animator) != 0 && U.Alive(o.Animator)) o.Animator.Play(u.AnimState, 0, 0f);
                if ((u.Mask & M_States) != 0 && o.States != null)
                    for (int i = 0; i < u.StateIdx.Length; i++)
                        if (u.StateIdx[i] < o.States.Count) try { o.States[u.StateIdx[i]].Write(u.StateVal[i]); } catch { }
                if ((u.Mask & M_Tag) != 0 && o.TrackTag) WriteTag(o, u.Tag);
            }
            catch (Exception e) { Log.Debug("ApplyAsHost: " + e.Message); }
            if ((u.Mask & M_Active) != 0 && u.Active && !wasActive) Reactions.OnActivated(o);
        }

        static void HandleClaim(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null) return;
            var id = r.UInt(); var reason = r.Byte();
            Objs.TryGetValue(id, out var o);
            // reason 1: the client's game released a body under that player (attic boards) – any synced body may be taken
            bool kind = o != null && (o.IsItem || o.IsSpawned || o.IsPickable || (reason == 1 && U.Alive(o.Rb) && o.TrackPose));
            bool ok = kind && U.Alive(o.Go) && !o.HostOnly && !o.IsContainer && (o.Owner == 255 || o.Owner == from.Id) && !from.Ghost && PlayerNear(from, o.Tr.position, reason == 2 ? 40f : 4f); // 2: hit by that player's arrow / thrown item
            var w = Session.W(Msg.OwnResult); w.UInt(id); w.Bool(ok);
            Session.SendTo(from.Id, w, true);
            if (o == null) return;
            if (ok) { SetOwner(o, from.Id); Log.Debug($"{from.Name} owns {o.Path}{(reason == 1 ? " (released by their game)" : "")}"); }
            else
            {
                Log.Debug($"Claim of {o.Path} by {from.Name} denied (owner {(o.Owner == 255 ? "-" : Session.NameOf(o.Owner))})");
                Session.SendTo(from.Id, ForcedDelta(o), true);
            }
        }

        static void HandleClaimResult(NetPlayer from, NetReader r)
        {
            var id = r.UInt(); var ok = r.Bool();
            if (!Objs.TryGetValue(id, out var o)) return;
            o.PendingClaim = false;
            if (ok) return;
            o.LocalReleased = false;
            // denied – someone was first: give the object back
            Log.Info("Item taken by another player: " + o.Go.name);
            Session.Notify(L.T("Someone else already took it!", "Ktoś inny już to wziął!"), false);
            ForceUnhold(o);
        }

        /// <summary>Undo a local pick-up (denied by the host).</summary>
        static void ForceUnhold(NetObj o)
        {
            byte keepOwner = o.Owner;
            o.Owner = 255;
            try
            {
                if (U.Alive(o.OrigParent) && U.IsUnder(o.Tr, GameRefs.PlayerRoot)) o.Tr.SetParent(o.OrigParent, true);
                var inv = GameRefs.Inventory;
                if (U.Alive(inv))
                {
                    var t = typeof(InventoryController);
                    foreach (var p in FieldProps(t))
                    {
                        if (p.PropertyType != typeof(GameObject)) continue;
                        var go = p.GetValue(inv) as GameObject;
                        if (go == null || go.Pointer != o.Go.Pointer) continue;
                        var have = t.GetProperty("have" + p.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        have?.SetValue(inv, false);
                    }
                }
            }
            catch (Exception e) { Log.Debug("ForceUnhold: " + e.Message); }
            Revert(o);
            if (keepOwner != Session.LocalId && keepOwner != 255) o.Owner = keepOwner; // still owned by another player
        }

        static void HandleRelease(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null) return;
            var id = r.UInt(); var act = r.Bool(); var pos = r.Vec3(); var rot = r.Quat(); var en = r.UShort();
            if (!Objs.TryGetValue(id, out var o) || o.Owner != from.Id) return;
            o.HasTarget = false;
            // the owner's game decided what the body is now: released boards are normal physics objects, a board lying
            // on the floor with its collider off was made kinematic again – the host takes that over
            if (U.Alive(o.Rb) && !o.HostOnly)
            {
                bool dyn = (en & DynBit) != 0;
                if (dyn == o.OrigKinematic) { o.OrigKinematic = !dyn; if (dyn) o.IsItem = true; }
            }
            SetOwner(o, 255); // first: it switches the colliders back on, the owner's collider state is applied after it
            ApplyAsHost(o, new ObjUpdate { Mask = M_Active | M_Pose | M_Enabled, Active = act, Pos = pos, Rot = rot, Enabled = en });
            // the released object rests where its owner left it: no settling on the host, no correction back to the owner
            if (U.Alive(o.Rb) && !o.Rb.isKinematic) { o.Rb.linearVelocity = Vector3.zero; o.Rb.angularVelocity = Vector3.zero; o.Rb.Sleep(); }
            o.APos = pos; o.ARot = rot; o.AActive = act; o.AOwnerSent = 255;
            var dw = Session.W(Msg.WorldDelta); dw.UShort(1); dw.Byte(0); WriteObj(dw, o, (byte)(M_Pose | M_Active | M_Owner)); Session.Broadcast(dw, true, from.Id);
            Log.Debug($"{from.Name} released {o.Path}");
        }

        static void HandleItemPose(NetPlayer from, NetReader r)
        {
            var id = r.UInt(); var pos = r.Vec3(); var rot = r.Quat(); var act = r.Bool();
            if (!Objs.TryGetValue(id, out var o) || !U.Alive(o.Go)) return;
            if (Session.IsHost)
            {
                if (from == null || o.Owner != from.Id) return;
                // forward
                var w = Session.W(Msg.ItemPose); w.UInt(id); w.Vec3(pos); w.Quat(rot); w.Bool(act);
                Session.Broadcast(w, false, from.Id);
                o.AOwnerSent = o.Owner;
            }
            else if (o.Owner == Session.LocalId) return;
            if (o.Go.activeSelf != act) o.Go.SetActive(act);
            o.TPos = pos; o.TRot = rot; o.HasTarget = true;
            if (U.Alive(o.Rb)) { o.Rb.isKinematic = true; o.RemoteDrivenUntil = Time.unscaledTime + 0.6f; }
            o.APos = pos; o.ARot = rot; o.AActive = act;
        }

        static void HandleAiPose(NetPlayer from, NetReader r)
        {
            if (!Session.IsClient) return;
            int n = r.Byte();
            for (int i = 0; i < n; i++)
            {
                var id = r.UInt(); var pos = r.Vec3(); var rot = r.Quat();
                if (!Objs.TryGetValue(id, out var o) || !U.Alive(o.Tr)) continue;
                if ((o.Tr.position - pos).sqrMagnitude > 16f) { o.Tr.position = pos; o.Tr.rotation = rot; o.HasTarget = false; }
                else { o.TPos = pos; o.TRot = rot; o.HasTarget = true; }
                o.APos = pos; o.ARot = rot;
            }
        }

        static void HandleSpawn(NetPlayer from, NetReader r)
        {
            if (!Session.IsClient || !Registered) return;
            var id = r.UInt(); var name = r.Str(); var pos = r.Vec3(); var rot = r.Quat(); var act = r.Bool();
            var owner = r.Byte(); var origin = r.Byte(); var tempId = r.UInt(); var vel = r.Vec3();
            var loaded = (sbyte)(r.Byte() - 1);
            if (Objs.ContainsKey(id)) return;
            // my own dropped item – bind the local clone to the id assigned by the host
            if (origin == Session.LocalId && _pendingSpawns.TryGetValue(tempId, out var mine))
            {
                _pendingSpawns.Remove(tempId);
                Objs.Remove(tempId); _trById.Remove(tempId);
                mine.Id = id; mine.PendingSpawn = false; mine.Owner = Session.LocalId;
                Objs[id] = mine;
                if (U.Alive(mine.Tr)) { _idByTr[mine.Tr.Pointer] = id; _trById[id] = mine.Tr; }
                return;
            }
            var go = InstantiateByName(name, pos, rot, act);
            if (go == null) { Log.Warn($"Spawn {name} -> {id}: no prefab/template found"); return; }
            var o = MakeSpawnObj(id, go, name);
            o.Loaded = loaded;
            AddObj(o);
            SetOwner(o, owner);
            Log.Debug($"Spawned {name} -> {id} (owner {(owner == 255 ? "-" : Session.NameOf(owner))}){(loaded >= 0 ? (loaded == 1 ? " loaded" : " empty") : "")}");
            if (owner != Session.LocalId && U.Alive(o.Rb)) { o.Rb.isKinematic = true; o.RemoteDrivenUntil = Time.unscaledTime + 1f; }
        }

        /// <summary>Host: a client dropped an item – spawn the same clone for everybody, the client keeps simulating it.</summary>
        static void HandleSpawnReq(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null || !Registered) return;
            var tempId = r.UInt(); var name = r.Str(); var pos = r.Vec3(); var rot = r.Quat(); var act = r.Bool();
            var loaded = (sbyte)(r.Byte() - 1);
            if (!GameFlow.InTransition && !PlayerNear(from, pos, 4f)) return; // at the end of the day items are dropped where the game puts them
            var go = InstantiateByName(name, pos, rot, act);
            if (go == null) return;
            var o = MakeSpawnObj(_nextSpawnId++, go, name);
            o.Loaded = Weapons.IsWeapon(name) ? loaded : (sbyte)-1;
            AddObj(o);
            SetOwner(o, from.Id);
            if (Weapons.IsWeapon(name)) _clientWeaponDrops.Add((name, pos, Time.unscaledTime, from.Name));
            var w = Session.W(Msg.Spawn); WriteSpawn(w, o, from.Id, tempId);
            Session.Broadcast(w, true);
            Log.Debug($"{from.Name} dropped {name} -> {o.Id}");
        }

        // a weapon a client dropped a moment ago – the same weapon appearing in the host's game right after it near the
        // same place is a copy (seen in a log: one dropped shotgun, 2–3 spawned for everybody)
        static readonly List<(string name, Vector3 pos, float at, string by)> _clientWeaponDrops = new();

        static bool DuplicateOfClientDrop(GameObject root)
        {
            if (_clientWeaponDrops.Count == 0) return false;
            float now = Time.unscaledTime;
            _clientWeaponDrops.RemoveAll(d => now - d.at > 5f);
            var name = BaseName(root.name);
            if (!Weapons.IsWeapon(name)) return false;
            foreach (var d in _clientWeaponDrops)
            {
                if (d.name != name || U.Dist(d.pos, root.transform.position) > 6f) continue;
                Log.Info($"A second {name} appeared in the host's game {now - d.at:F1} s after {d.by} dropped one, {U.Dist(d.pos, root.transform.position):F1} m away – removed as a copy");
                UnityEngine.Object.Destroy(root);
                return true;
            }
            return false;
        }

        /// <summary>Host: a client picked up (destroyed) an object.</summary>
        static void HandleDestroyReq(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null) return;
            var id = r.UInt();
            if (!Objs.TryGetValue(id, out var o) || !U.Alive(o.Go)) return;
            if (from.Ghost || o.HostOnly || !PlayerNear(from, o.Tr.position, 4f)) { Log.Debug($"Rejected destroy of {o.Path} by {from.Name}"); return; }
            o.ExpectDestroy = true; // picked up by that client – not by the host
            UnityEngine.Object.Destroy(o.Go); // HostTick notices it and broadcasts Despawn to everyone
            Log.Debug($"{from.Name} picked up {o.Path}");
        }

        public static void OnPlayerLeft(byte id)
        {
            foreach (var o in _list)
                if (o.Owner == id) SetOwner(o, 255);
        }

        public static int Count => _list.Count;

        // ======================= debugging =======================
        public static string Summary()
        {
            int mine = 0, remote = 0, driven = 0, released = 0, pending = 0, items = 0, reverts = 0, groups = 0;
            float now = Time.unscaledTime;
            foreach (var o in _list)
            {
                if (o.IsContainer) { groups++; continue; }
                if (o.IsItem) items++;
                if (o.Owner == Session.LocalId) mine++; else if (o.Owner != 255) remote++;
                if (o.HasTarget) driven++;
                if (o.LocalReleased) released++;
                if (o.PendingSpawn) pending++;
                reverts += o.Reverts;
            }
            return $"objects {_list.Count} (items {items}, groups {groups}) · mine {mine} (released by game {released}) · others' {remote} · interpolating {driven} · pending spawns {pending} · reverts {reverts}"
                   + (Session.IsClient ? $" · proposals {(now > SuppressProposalsUntil ? "on" : "SUPPRESSED")}{(now < StartupUntil ? " · startup" : "")}" : "");
        }

        public static void Dump(System.Text.StringBuilder sb)
        {
            float now = Time.unscaledTime;
            sb.AppendLine($"# world: {Summary()}");
            sb.AppendLine("id	path	owner	active	pos	syncPos	rb	kinematic	origKin	item	track	hostOnly	world	pickable	spawned	interp	gameReleased	reverts	lastMask	group	tag	pendingClaim");
            foreach (var o in _list)
            {
                if (!U.Alive(o.Go)) { sb.AppendLine($"{o.Id}	{o.Path}	(destroyed)"); continue; }
                var p = o.Tr.position;
                bool rb = U.Alive(o.Rb);
                sb.AppendLine($"{o.Id}	{o.Path}	{(o.Owner == 255 ? "-" : Session.NameOf(o.Owner))}	{o.Go.activeSelf}	{p.x:F2},{p.y:F2},{p.z:F2}	{o.APos.x:F2},{o.APos.y:F2},{o.APos.z:F2}	{rb}	{(rb && o.Rb.isKinematic)}	{o.OrigKinematic}	{o.IsItem}	{o.TrackPose}	{o.HostOnly}	{o.IsWorld}	{o.IsPickable}	{o.IsSpawned}	{o.HasTarget}	{o.LocalReleased}	{o.Reverts}	{o.LastDiffMask}	{o.IsContainer}	{(o.TrackTag ? ReadTag(o) + (ReadTag(o) != o.ATag ? "≠" + o.ATag : "") : "")}	{o.PendingClaim}");
            }
        }
    }
}
