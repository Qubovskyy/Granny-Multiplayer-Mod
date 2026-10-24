using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// Player models shown for the other players: skinned mesh, texture and animations (idle, walking, crouching)
    /// read from models\*.gmdl next to the mod – the bundled "male" / "female" and any custom model put there.
    /// The files are made from FBX models with tools\ModelExport.cs in a Unity editor (the game cannot import FBX at
    /// runtime); the bones are animated here frame by frame.
    /// </summary>
    internal static class PlayerModels
    {
        internal struct BW4 { public int I0, I1, I2, I3; public float W0, W1, W2, W3; }

        internal sealed class Clip { public string Name; public float Fps; public int Frames; public Quaternion[] Rot; public Vector3[] Hips; }

        internal sealed class Data
        {
            public string Name;
            public string[] Names; public int[] Parent; public Vector3[] P; public Quaternion[] R; public Vector3[] S;
            public int SmrNode, RootBone, Hips; public int[] Bones; public Matrix4x4[] BindPoses;
            // the bones that turn when the player looks around (upper spine, neck, head), -1 = not in this model
            public int Spine2 = -1, Neck = -1, Head = -1;
            // finger bones: they keep their own (straight) pose – see Animate
            public bool[] Finger;
            public Vector3[] V, N; public Vector2[] UV; public BW4[] BW; public int[][] Tris;
            public int TexW, TexH; public byte[] Tex;
            public float Height;
            public readonly Dictionary<string, Clip> Clips = new();
            public Texture2D Texture; public Material Material;
            public System.Numerics.Matrix4x4[] BindN;
        }

        public const string Default = "male";
        static readonly Dictionary<string, Data> _data = new(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
        static Shader _shader;
        // Objects created here are referenced from the IL2CPP side too: a texture / mesh / material that only a managed
        // field points at can be collected by the game's garbage collector ("Object was garbage collected in IL2CPP domain")
        static readonly KeepList _keep = new();
        // (a strong IL2CPP GC handle per object – an IL2CPP list held only by a managed field gets collected itself)
        sealed class KeepList
        {
            readonly Dictionary<IntPtr, IntPtr> _h = new();
            public void Add(UnityEngine.Object o)
            {
                if (o == null) return;
                var p = o.Pointer;
                if (p != IntPtr.Zero && !_h.ContainsKey(p)) _h[p] = Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_new(p, false);
            }
            public void Remove(UnityEngine.Object o)
            {
                if (o == null) return;
                var p = o.Pointer;
                if (_h.TryGetValue(p, out var h)) { Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_free(h); _h.Remove(p); }
            }
        }

        public static string Dir => Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "models");

        static List<string> _list; static float _listAt;
        /// <summary>Names of the available models (file names in the models folder): male, female, then custom ones.</summary>
        public static List<string> Available
        {
            get
            {
                if (_list != null && Time.unscaledTime - _listAt < 5f) return _list;
                _listAt = Time.unscaledTime;
                var l = new List<string>();
                try { if (Directory.Exists(Dir)) l.AddRange(Directory.GetFiles(Dir, "*.gmdl").Select(Path.GetFileNameWithoutExtension)); } catch { }
                int Rank(string n) => n.Equals("male", StringComparison.OrdinalIgnoreCase) ? 0 : n.Equals("female", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                return _list = l.OrderBy(Rank).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        /// <summary>Shown name of a model: the bundled ones translated, the others by their file name ("Character_17_Police" → "Character 17 Police").</summary>
        public static string Title(string name) =>
            name.Equals("male", StringComparison.OrdinalIgnoreCase) ? L.T("Man", "Mężczyzna")
            : name.Equals("female", StringComparison.OrdinalIgnoreCase) ? L.T("Woman", "Kobieta") : (name ?? "").Replace('_', ' ');

        public static Data Get(string name)
        {
            if (string.IsNullOrEmpty(name)) name = Default;
            if (_data.TryGetValue(name, out var d)) return d;
            if (_failed.Contains(name)) return name == Default ? null : Get(Default);
            var file = Path.Combine(Dir, name + ".gmdl");
            try
            {
                if (!File.Exists(file)) throw new FileNotFoundException("missing file");
                d = Read(file);
                d.Name = name;
                _data[name] = d;
                Log.Info($"Player model '{name}': {d.Names.Length} bones, {d.V.Length} vertices, {d.Clips.Count} animations");
                return d;
            }
            catch (Exception e)
            {
                _failed.Add(name);
                Log.Warn($"Player model '{name}' ({file}): {e.Message}" + (name == Default ? " – the simple avatar is used" : " – the default model is used"));
                return name == Default ? null : Get(Default);
            }
        }

        /// <summary>Index of the bone named <paramref name="name"/> ("mixamorig:Head", "Head", "Bip01_Head"…), -1 when missing.</summary>
        static int Bone(string[] names, string name) => Array.FindIndex(names, x =>
            x.Equals(name, StringComparison.OrdinalIgnoreCase) || x.EndsWith(":" + name, StringComparison.OrdinalIgnoreCase) || x.EndsWith("_" + name, StringComparison.OrdinalIgnoreCase));

        static Vector3 V3(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        static Quaternion Q(BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        static Data Read(string file)
        {
            using var r = new BinaryReader(File.OpenRead(file));
            if (r.ReadInt32() != 0x4C444D47) throw new Exception("not a GMDL file");
            if (r.ReadInt32() != 1) throw new Exception("unknown version");
            var d = new Data();
            int n = r.ReadInt32();
            d.Names = new string[n]; d.Parent = new int[n]; d.P = new Vector3[n]; d.R = new Quaternion[n]; d.S = new Vector3[n];
            for (int i = 0; i < n; i++) { d.Names[i] = r.ReadString(); d.Parent[i] = r.ReadInt32(); d.P[i] = V3(r); d.R[i] = Q(r); d.S[i] = V3(r); }
            d.Hips = Array.FindIndex(d.Names, x => x.EndsWith("Hips", StringComparison.OrdinalIgnoreCase));
            d.Spine2 = Bone(d.Names, "Spine2"); d.Neck = Bone(d.Names, "Neck"); d.Head = Bone(d.Names, "Head");
            d.Finger = d.Names.Select(x => x.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) >= 0
                && new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" }.Any(f => x.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
            d.SmrNode = r.ReadInt32(); d.RootBone = r.ReadInt32();
            int nb = r.ReadInt32();
            d.Bones = new int[nb]; for (int i = 0; i < nb; i++) d.Bones[i] = r.ReadInt32();
            d.BindPoses = new Matrix4x4[nb];
            for (int i = 0; i < nb; i++) { var m = new Matrix4x4(); for (int k = 0; k < 16; k++) m[k] = r.ReadSingle(); d.BindPoses[i] = m; }
            int nv = r.ReadInt32();
            d.V = new Vector3[nv]; d.N = new Vector3[nv]; d.UV = new Vector2[nv]; d.BW = new BW4[nv];
            float top = 0f;
            for (int i = 0; i < nv; i++)
            {
                d.V[i] = V3(r); d.N[i] = V3(r); d.UV[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                var b = new BW4 { I0 = r.ReadInt32(), I1 = r.ReadInt32(), I2 = r.ReadInt32(), I3 = r.ReadInt32() };
                b.W0 = r.ReadSingle(); b.W1 = r.ReadSingle(); b.W2 = r.ReadSingle(); b.W3 = r.ReadSingle();
                d.BW[i] = b;
                if (d.V[i].y > top) top = d.V[i].y;
            }
            d.Height = Mathf.Max(0.1f, top);
            int ns = r.ReadInt32();
            d.Tris = new int[ns][];
            for (int s = 0; s < ns; s++) { int c = r.ReadInt32(); var t = new int[c]; for (int k = 0; k < c; k++) t[k] = r.ReadInt32(); d.Tris[s] = t; }
            d.TexW = r.ReadInt32(); d.TexH = r.ReadInt32();
            d.Tex = r.ReadBytes(d.TexW * d.TexH * 4);
            int nc = r.ReadInt32();
            for (int c = 0; c < nc; c++)
            {
                var cl = new Clip { Name = r.ReadString(), Fps = r.ReadSingle(), Frames = r.ReadInt32() };
                cl.Rot = new Quaternion[cl.Frames * n]; cl.Hips = new Vector3[cl.Frames];
                for (int f = 0; f < cl.Frames; f++)
                {
                    for (int i = 0; i < n; i++) cl.Rot[f * n + i] = Q(r);
                    cl.Hips[f] = V3(r);
                }
                d.Clips[cl.Name] = cl;
            }
            if (!d.Clips.ContainsKey("crouchwalk")) MakeCrouchWalk(d);
            return d;
        }

        const float CrouchShare = 0.65f;

        /// <summary>
        /// Walking crouched, made from the clips the model has (a model may bring its own "crouchwalk"): every bone between
        /// the walk and the crouch pose (65 % crouched), the hips at the height in between. Checked on the bundled models:
        /// the feet stay on the floor and step forward / back, the body stays low.
        /// </summary>
        static void MakeCrouchWalk(Data d)
        {
            if (!d.Clips.TryGetValue("walk", out var walk) || !d.Clips.TryGetValue("crouch", out var crouch)) return;
            int n = d.Names.Length, frames = walk.Frames;
            if (frames < 2 || crouch.Frames < 1) return;
            var cw = new Clip { Name = "crouchwalk", Fps = walk.Fps, Frames = frames, Rot = new Quaternion[frames * n], Hips = new Vector3[frames] };
            for (int f = 0; f < frames; f++)
            {
                int cf = f % crouch.Frames;
                for (int i = 0; i < n; i++)
                {
                    var q = System.Numerics.Quaternion.Slerp(SQ(walk.Rot[f * n + i]), SQ(crouch.Rot[cf * n + i]), CrouchShare);
                    cw.Rot[f * n + i] = new Quaternion(q.X, q.Y, q.Z, q.W);
                }
                var c = crouch.Hips[cf]; var w = walk.Hips[f];
                cw.Hips[f] = new Vector3(c.x, w.y + (c.y - w.y) * CrouchShare, c.z);
            }
            d.Clips["crouchwalk"] = cw;
        }

        static Texture2D Texture(Data d)
        {
            if (U.Alive(d.Texture)) return d.Texture;
            // no mipmaps: the raw data holds exactly one image
            var t = new Texture2D(d.TexW, d.TexH, TextureFormat.RGBA32, false);
            t.hideFlags = HideFlags.HideAndDontSave;
            _keep.Add(t);
            bool ok = false;
            try { t.LoadRawTextureData(d.Tex); ok = true; } catch (Exception e) { Log.Debug("model texture (raw): " + e.Message); }
            if (!ok)
            {
                try
                {
                    var px = new Color32[d.TexW * d.TexH];
                    for (int i = 0; i < px.Length; i++) px[i] = new Color32(d.Tex[i * 4], d.Tex[i * 4 + 1], d.Tex[i * 4 + 2], d.Tex[i * 4 + 3]);
                    t.SetPixels32(px); ok = true;
                }
                catch (Exception e) { Log.Debug("model texture (pixels): " + e.Message); }
            }
            if (!ok)
                for (int y = 0; y < d.TexH; y++)
                    for (int x = 0; x < d.TexW; x++)
                    {
                        int i = (y * d.TexW + x) * 4;
                        t.SetPixel(x, y, new Color32(d.Tex[i], d.Tex[i + 1], d.Tex[i + 2], d.Tex[i + 3]));
                    }
            t.Apply(false);
            return d.Texture = t;
        }

        const string MatteShader = "Standard (Specular setup)";

        /// <summary>
        /// A lit shader the game really contains. The specular Standard first (the game scene has it): with a black
        /// specular colour it has no highlights and no reflections at all. Else Granny's own, the standard one, or one
        /// used in the scene.
        /// </summary>
        static Shader LitShader()
        {
            if (_shader != null) { try { if (_shader.name == MatteShader) return _shader; } catch { _shader = null; } }
            // the menu does not load it – the fallback is used there and the matte one as soon as the game scene has it
            try { var sp = Shader.Find(MatteShader); if (sp != null) { _keep.Add(sp); return _shader = sp; } } catch { }
            if (_shader != null) { try { if (_shader.name != null) return _shader; } catch { } _shader = null; }
            try
            {
                if (U.Alive(GameRefs.Granny))
                    foreach (var c in GameRefs.Granny.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<SkinnedMeshRenderer>(), true))
                    {
                        var m = c.TryCast<SkinnedMeshRenderer>()?.sharedMaterial;
                        if (m != null && m.shader != null) { _shader = m.shader; break; }
                    }
            }
            catch { }
            if (_shader == null) { try { _shader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse"); } catch { } }
            if (_shader == null)
            {
                try
                {
                    foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<Renderer>()))
                    {
                        var m = o.TryCast<Renderer>()?.sharedMaterial;
                        if (m == null || m.shader == null) continue;
                        var sn = m.shader.name ?? "";
                        if (sn.Contains("Standard") || sn.Contains("Diffuse")) { _shader = m.shader; break; }
                    }
                }
                catch { }
            }
            if (_shader != null) _keep.Add(_shader);
            return _shader;
        }

        // a light texture under the flashlight glowed white – the surface takes a bit less light than the texture says
        static readonly Color Tint = new Color(0.8f, 0.8f, 0.8f, 1f);

        static Material MaterialOf(Data d)
        {
            var sh = LitShader();
            if (U.Alive(d.Material) && (sh == null || d.Material.shader == sh)) return d.Material;
            if (sh == null) throw new Exception("no shader");
            var tex = Texture(d);
            var m = new Material(sh);
            _keep.Add(m);
            m.hideFlags = HideFlags.HideAndDontSave;
            m.mainTexture = tex;
            try { m.color = Tint; } catch { }
            // matte: smoothness 0, no metal and no specular colour – no highlights, no reflections
            Float(m, "_Glossiness", 0f); Float(m, "_GlossMapScale", 0f); Float(m, "_Smoothness", 0f); Float(m, "_Metallic", 0f);
            Float(m, "_SpecularHighlights", 0f); Float(m, "_GlossyReflections", 0f);
            // legacy specular shaders: a black specular colour, the narrowest highlight (0 would light the whole model)
            Float(m, "_Shininess", 1f);
            try { int id = Shader.PropertyToID("_SpecColor"); if (m.HasProperty(id)) m.SetColor(id, Color.black); } catch { }
            try { m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF"); m.EnableKeyword("_GLOSSYREFLECTIONS_OFF"); } catch { }
            try { m.SetTexture("_MetallicGlossMap", null); m.SetTexture("_SpecGlossMap", null); m.SetTexture("_BumpMap", null); } catch { }
            try { m.DisableKeyword("_METALLICGLOSSMAP"); m.DisableKeyword("_SPECGLOSSMAP"); m.DisableKeyword("_NORMALMAP"); m.DisableKeyword("_EMISSION"); } catch { }
            // (a material made earlier with the menu's shader stays – a preview may still show it)
            Log.Debug($"Player model material: {sh.name}");
            return d.Material = m;
        }

        static void Float(Material m, string name, float v)
        {
            try { int id = Shader.PropertyToID(name); if (m.HasProperty(id)) m.SetFloat(id, v); } catch { }
        }

        /// <summary>A built model: its bones and renderers, animated by <see cref="Animate"/>.</summary>
        internal sealed class Instance
        {
            public string Name; public Data D; public GameObject Go; public Transform[] Nodes; public Renderer[] Rends;
            public Mesh Mesh; public System.Numerics.Quaternion[] Local; public Vector3 HipsPos;
            /// <summary>The lowest point of the animated model (model space, smoothed) – where its feet touch the floor.</summary>
            public float MinY = float.NaN;
            public System.Numerics.Matrix4x4[] G, K; public Vector3[] OutV, OutN;
            public string Cur = "idle", Prev; public float CurT, PrevT, Blend = 1f;
            /// <summary>Where the head looks relative to the body (degrees; pitch &gt; 0 = down, yaw &gt; 0 = right).</summary>
            public float LookPitch, LookYaw;
        }

        // how the look is shared between the upper spine, the neck and the head (together the whole angle)
        const float LookSpine = 0.2f, LookNeck = 0.35f, LookHead = 0.45f;

        /// <summary>Turns the model's head (with the neck and the upper spine) – applied with the next animation frame.</summary>
        public static void SetLook(Instance m, float pitch, float yaw)
        {
            if (m == null) return;
            m.LookPitch = pitch; m.LookYaw = yaw;
        }

        public static Instance Build(string name, Transform parent)
        {
            var d = Get(name);
            if (d == null) return null;
            string step = "bones";
            try
            {
                var inst = new Instance { Name = d.Name, D = d };
                int n = d.Names.Length;
                inst.Nodes = new Transform[n];
                for (int i = 0; i < n; i++)
                {
                    var go = new GameObject(i == 0 ? "Model" : d.Names[i]);
                    inst.Nodes[i] = go.transform;
                    go.transform.SetParent(i == 0 || d.Parent[i] < 0 ? parent : inst.Nodes[d.Parent[i]], false);
                    go.transform.localPosition = d.P[i]; go.transform.localRotation = d.R[i]; go.transform.localScale = d.S[i];
                }
                inst.Go = inst.Nodes[0].gameObject;
                step = "material"; var mat = MaterialOf(d);
                step = "mesh";
                var mesh = new Mesh();
                mesh.name = "GrannyMP_PlayerMesh";
                _keep.Add(mesh);
                mesh.vertices = d.V; mesh.normals = d.N; mesh.uv = d.UV;
                mesh.subMeshCount = d.Tris.Length;
                for (int s = 0; s < d.Tris.Length; s++) mesh.SetTriangles(d.Tris[s], s);
                mesh.RecalculateBounds();
                // skinning is computed here (managed code) and written into an ordinary mesh every frame: the engine's
                // own skinning setup (bone weights) is stripped from this game's build
                step = "renderer";
                inst.Go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = inst.Go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                inst.Mesh = mesh;
                inst.Rends = new Renderer[] { mr };
                inst.Local = new System.Numerics.Quaternion[n];
                for (int i = 0; i < n; i++) inst.Local[i] = new System.Numerics.Quaternion(d.R[i].x, d.R[i].y, d.R[i].z, d.R[i].w);
                inst.HipsPos = d.Hips >= 0 ? d.P[d.Hips] : Vector3.zero;
                inst.G = new System.Numerics.Matrix4x4[n];
                inst.OutV = new Vector3[d.V.Length]; inst.OutN = new Vector3[d.V.Length];
                step = "skinning";
                Skin(inst);
                Log.Debug($"Player model '{d.Name}' built");
                return inst;
            }
            catch (Exception e) { Log.Warn($"Player model build ({step}): {e.Message}"); return null; }
        }

        /// <summary>Plays animation <paramref name="clip"/> (cross-fading from the previous one) at <paramref name="speed"/>.</summary>
        public static void Animate(Instance m, string clip, float speed, float dt)
        {
            if (m == null || !U.Alive(m.Go)) return;
            if (!m.D.Clips.ContainsKey(clip)) clip = clip == "crouchwalk" && m.D.Clips.ContainsKey("crouch") ? "crouch" : "idle";
            if (!m.D.Clips.ContainsKey(clip)) return; // a custom model without animations stays in its own pose
            if (clip != m.Cur) { m.Prev = m.Cur; m.PrevT = m.CurT; m.Cur = clip; m.CurT = 0f; m.Blend = 0f; }
            m.CurT += dt * speed; m.PrevT += dt;
            m.Blend = Mathf.Min(1f, m.Blend + dt / 0.25f);
            var d = m.D; int n = d.Names.Length;
            Sample(d, m.Cur, m.CurT, out int fa, out int fb, out float fr);
            var c = d.Clips[m.Cur];
            Clip p = null; int pa = 0, pb = 0; float pr = 0f;
            if (m.Blend < 1f && m.Prev != null && d.Clips.TryGetValue(m.Prev, out p)) Sample(d, m.Prev, m.PrevT, out pa, out pb, out pr);
            for (int i = 1; i < n; i++)
            {
                // the fingers keep the model's own straight pose: Mixamo animations curl a full hand, and a model whose
                // hand is one mitten on the index-finger bones (the bundled ones) then bends it into a hook
                if (d.Finger != null && d.Finger[i]) { m.Local[i] = SQ(d.R[i]); continue; }
                var q = System.Numerics.Quaternion.Slerp(SQ(c.Rot[fa * n + i]), SQ(c.Rot[fb * n + i]), fr);
                if (p != null) q = System.Numerics.Quaternion.Slerp(System.Numerics.Quaternion.Slerp(SQ(p.Rot[pa * n + i]), SQ(p.Rot[pb * n + i]), pr), q, m.Blend);
                m.Local[i] = q;
            }
            if (d.Hips >= 0)
            {
                var h = Vector3.Lerp(c.Hips[fa], c.Hips[fb], fr);
                if (p != null) h = Vector3.Lerp(Vector3.Lerp(p.Hips[pa], p.Hips[pb], pr), h, m.Blend);
                m.HipsPos = h;
            }
            Skin(m);
        }

        static System.Numerics.Quaternion SQ(Quaternion q) => new System.Numerics.Quaternion(q.x, q.y, q.z, q.w);
        static System.Numerics.Vector3 SV(Vector3 v) => new System.Numerics.Vector3(v.x, v.y, v.z);

        /// <summary>
        /// Skinning in managed code: the pose of every node (relative to the model root) times the bone's bind pose, the
        /// vertices blended by their bone weights, written into the model's mesh. (Row-vector matrices: v' = v · M.)
        /// </summary>
        static void Skin(Instance m)
        {
            var d = m.D; int n = d.Names.Length;
            if (d.BindN == null)
            {
                d.BindN = new System.Numerics.Matrix4x4[d.BindPoses.Length];
                for (int b = 0; b < d.BindPoses.Length; b++)
                {
                    // Unity's matrix is column-major with column vectors – its transpose in row-vector form
                    var u = d.BindPoses[b];
                    d.BindN[b] = new System.Numerics.Matrix4x4(u[0], u[1], u[2], u[3], u[4], u[5], u[6], u[7], u[8], u[9], u[10], u[11], u[12], u[13], u[14], u[15]);
                }
            }
            bool look = Mathf.Abs(m.LookPitch) > 0.01f || Mathf.Abs(m.LookYaw) > 0.01f;
            for (int i = 0; i < n; i++)
            {
                var pos = i == d.Hips ? m.HipsPos : d.P[i];
                var local = System.Numerics.Matrix4x4.CreateScale(SV(d.S[i])) * System.Numerics.Matrix4x4.CreateFromQuaternion(m.Local[i]) * System.Numerics.Matrix4x4.CreateTranslation(SV(pos));
                // node 0 is the model root itself (its own transform is applied by Unity)
                m.G[i] = i == 0 ? System.Numerics.Matrix4x4.Identity : (d.Parent[i] <= 0 ? local : local * m.G[d.Parent[i]]);
                if (look && i > 0)
                {
                    float w = i == d.Head ? LookHead : i == d.Neck ? LookNeck : i == d.Spine2 ? LookSpine : 0f;
                    if (w > 0f) m.G[i] = TurnAt(m.G[i], Quaternion.Euler(m.LookPitch * w, m.LookYaw * w, 0f));
                }
            }
            int nb = d.Bones.Length;
            if (m.K == null || m.K.Length != nb) m.K = new System.Numerics.Matrix4x4[nb];
            for (int b = 0; b < nb; b++) m.K[b] = d.BindN[b] * m.G[d.Bones[b]];
            var bw = d.BW; var V = d.V; var N = d.N;
            for (int v = 0; v < V.Length; v++)
            {
                var w = bw[v]; var pv = SV(V[v]); var pn = SV(N[v]);
                var sp = System.Numerics.Vector3.Zero; var sn = System.Numerics.Vector3.Zero;
                if (w.W0 > 0f && (uint)w.I0 < (uint)nb) { sp += System.Numerics.Vector3.Transform(pv, m.K[w.I0]) * w.W0; sn += System.Numerics.Vector3.TransformNormal(pn, m.K[w.I0]) * w.W0; }
                if (w.W1 > 0f && (uint)w.I1 < (uint)nb) { sp += System.Numerics.Vector3.Transform(pv, m.K[w.I1]) * w.W1; sn += System.Numerics.Vector3.TransformNormal(pn, m.K[w.I1]) * w.W1; }
                if (w.W2 > 0f && (uint)w.I2 < (uint)nb) { sp += System.Numerics.Vector3.Transform(pv, m.K[w.I2]) * w.W2; sn += System.Numerics.Vector3.TransformNormal(pn, m.K[w.I2]) * w.W2; }
                if (w.W3 > 0f && (uint)w.I3 < (uint)nb) { sp += System.Numerics.Vector3.Transform(pv, m.K[w.I3]) * w.W3; sn += System.Numerics.Vector3.TransformNormal(pn, m.K[w.I3]) * w.W3; }
                m.OutV[v] = new Vector3(sp.X, sp.Y, sp.Z);
                float len = sn.Length();
                m.OutN[v] = len > 1e-6f ? new Vector3(sn.X / len, sn.Y / len, sn.Z / len) : N[v];
            }
            float minY = float.MaxValue;
            for (int v = 0; v < m.OutV.Length; v++) if (m.OutV[v].y < minY) minY = m.OutV[v].y;
            if (minY < float.MaxValue) m.MinY = float.IsNaN(m.MinY) ? minY : Mathf.Lerp(m.MinY, minY, 0.25f);
            if (!U.Alive(m.Mesh)) return;
            m.Mesh.vertices = m.OutV;
            m.Mesh.normals = m.OutN;
            m.Mesh.RecalculateBounds();
        }

        /// <summary>
        /// The bone's pose turned by <paramref name="q"/> around its own joint, in model space (the model faces +Z,
        /// so pitch is about +X and yaw about +Y); the bones below it follow, as they are computed afterwards.
        /// </summary>
        static System.Numerics.Matrix4x4 TurnAt(System.Numerics.Matrix4x4 g, Quaternion q)
        {
            var pivot = new System.Numerics.Vector3(g.M41, g.M42, g.M43);
            return g * System.Numerics.Matrix4x4.CreateTranslation(-pivot) * System.Numerics.Matrix4x4.CreateFromQuaternion(SQ(q)) * System.Numerics.Matrix4x4.CreateTranslation(pivot);
        }

        static void Sample(Data d, string clip, float t, out int a, out int b, out float f)
        {
            var c = d.Clips[clip];
            float len = (c.Frames - 1) / c.Fps;
            float x = len > 0 ? Mathf.Repeat(t, len) * c.Fps : 0f;
            a = Mathf.Clamp((int)x, 0, c.Frames - 1); b = Mathf.Min(a + 1, c.Frames - 1); f = x - a;
        }

        // ================= previews for the character choice window =================
        // Each model is built far below the world with its own camera (rendering into a texture) and a small light.
        sealed class Preview { public GameObject Root; public Instance M; public Camera Cam; public RenderTexture Rt; }
        static readonly Dictionary<string, Preview> _previews = new(StringComparer.OrdinalIgnoreCase);
        static float _spin; static int _previewFrame;
        static readonly Dictionary<string, float> _previewFailedAt = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The preview picture of <paramref name="name"/> (built on first use, animated while shown).</summary>
        public static Texture PreviewTexture(string name)
        {
            if (_previews.TryGetValue(name, out var p) && U.Alive(p.Root) && U.Alive(p.Rt)) return p.Rt;
            // a model that could not be built is not tried again every frame
            if (_previewFailedAt.TryGetValue(name, out var ft) && Time.unscaledTime - ft < 10f) return null;
            try
            {
                int index = _previews.Count;
                p = new Preview();
                p.Root = new GameObject("GrannyMP_Preview_" + name);
                UnityEngine.Object.DontDestroyOnLoad(p.Root);
                p.Root.transform.position = new Vector3(index * 20f, -5000f, 0f);
                var holder = new GameObject("Holder"); holder.transform.SetParent(p.Root.transform, false);
                p.M = Build(name, holder.transform);
                if (p.M == null) { UnityEngine.Object.Destroy(p.Root); _previewFailedAt[name] = Time.unscaledTime; return null; }
                float h = 1.8f, s = h / p.M.D.Height;
                p.M.Go.transform.localScale = Vector3.one * s;
                p.M.Go.transform.localRotation = Quaternion.identity; // models face +Z – towards the camera
                var camGo = new GameObject("Camera"); camGo.transform.SetParent(p.Root.transform, false);
                camGo.transform.localPosition = new Vector3(0, 0.95f, 2.75f);
                camGo.transform.localRotation = Quaternion.LookRotation(new Vector3(0, -0.05f, -1f));
                p.Cam = camGo.AddComponent<Camera>();
                p.Cam.clearFlags = CameraClearFlags.SolidColor; p.Cam.backgroundColor = new Color(0.1f, 0.1f, 0.12f, 1f);
                p.Cam.fieldOfView = 38f; p.Cam.nearClipPlane = 0.1f; p.Cam.farClipPlane = 8f; p.Cam.depth = -100;
                p.Rt = new RenderTexture(400, 600, 16); // 2:3, sharp enough for the cards of the character window
                p.Rt.hideFlags = HideFlags.HideAndDontSave;
                _keep.Add(p.Rt);
                p.Cam.targetTexture = p.Rt;
                var lg = new GameObject("Light"); lg.transform.SetParent(p.Root.transform, false);
                lg.transform.localPosition = new Vector3(0.8f, 2.2f, 2.2f);
                var l = lg.AddComponent<Light>(); l.type = LightType.Point; l.range = 7f; l.intensity = 1.6f; l.renderMode = LightRenderMode.ForcePixel;
                var lg2 = new GameObject("Fill"); lg2.transform.SetParent(p.Root.transform, false);
                lg2.transform.localPosition = new Vector3(-1.2f, 1.2f, 1.5f);
                var l2 = lg2.AddComponent<Light>(); l2.type = LightType.Point; l2.range = 5f; l2.intensity = 0.6f; l2.renderMode = LightRenderMode.ForcePixel;
                _previews[name] = p;
                return p.Rt;
            }
            catch (Exception e) { Log.Warn("Model preview: " + e.Message); _previewFailedAt[name] = Time.unscaledTime; return null; }
        }

        /// <summary>
        /// While the choice window is open: the previews turn slowly and play their idle animation. Only those in
        /// <paramref name="shown"/> (on screen) are rendered.
        /// </summary>
        public static void UpdatePreviews(ICollection<string> shown = null)
        {
            if (_previews.Count == 0) return;
            if (shown != null)
                foreach (var kv in _previews)
                    if (U.Alive(kv.Value.Cam)) { bool on = shown.Contains(kv.Key); if (kv.Value.Cam.enabled != on) kv.Value.Cam.enabled = on; }
            if (Time.frameCount == _previewFrame) return; // OnGUI runs several times a frame
            _previewFrame = Time.frameCount;
            float dt = Time.unscaledDeltaTime;
            _spin += dt * 25f;
            foreach (var p in _previews.Values)
            {
                if (!U.Alive(p.Root) || p.M == null) continue;
                if (U.Alive(p.Cam) && !p.Cam.enabled) continue;
                Animate(p.M, "idle", 1f, dt);
                var holder = p.M.Go.transform.parent;
                if (holder != null) holder.localRotation = Quaternion.Euler(0, _spin, 0);
            }
        }

        /// <summary>The choice window was closed: the preview models and cameras go away.</summary>
        public static void ClearPreviews()
        {
            foreach (var p in _previews.Values)
            {
                try { if (U.Alive(p.Cam)) p.Cam.targetTexture = null; if (U.Alive(p.Rt)) { p.Rt.Release(); _keep.Remove(p.Rt); UnityEngine.Object.Destroy(p.Rt); } if (U.Alive(p.Root)) UnityEngine.Object.Destroy(p.Root); } catch { }
            }
            _previews.Clear();
        }
    }
}
