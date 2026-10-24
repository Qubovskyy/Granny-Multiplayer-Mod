using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace GrannyModelTool
{
    /// <summary>What to convert: the character FBX, its texture and the idle / walk / crouch animations.</summary>
    sealed class ExportJob
    {
        public string Model, Texture, Idle, Walk, Crouch, CrouchWalk, Output;
        /// <summary>Animations not given use the tool's built-in ones (DefaultAnims).</summary>
        public bool BuiltInAnimations = true;
        /// <summary>Hips height the animations were made for (default: the animation file's own hips).</summary>
        public float? AnimHipsOverride;

        public static readonly (string key, string part)[] ClipKinds = { ("idle", "Idle"), ("walk", "Walking"), ("crouch", "Crouch") };

        /// <summary>Missing animations / texture / output are looked for next to the model, like the Unity script did.</summary>
        public void FillDefaults(bool searchAnimations = true)
        {
            if (string.IsNullOrEmpty(Model)) return;
            if (searchAnimations)
            {
                Idle ??= FindAnimation("Idle"); Walk ??= FindAnimation("Walking") ?? FindAnimation("Walk"); Crouch ??= FindAnimation("Crouch");
                CrouchWalk ??= FindAnimation("CrouchWalk");
            }
            Texture ??= FindTexture();
            Output ??= System.IO.Path.ChangeExtension(Model, ".gmdl");
        }

        IEnumerable<string> SearchFolders()
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Model));
            var list = new List<string> { dir };
            var parent = System.IO.Path.GetDirectoryName(dir);
            foreach (var sub in new[] { "Animations", "Animation", "Anims", "Textures", "Texture" })
            {
                list.Add(System.IO.Path.Combine(dir, sub));
                if (parent != null) list.Add(System.IO.Path.Combine(parent, sub));
            }
            return list.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>An animation FBX next to the model: "Idle", "Walking" / "Walk", "Crouch" (crouching idle) or "CrouchWalk" (crouched walking).</summary>
        public string FindAnimation(string part)
        {
            var model = System.IO.Path.GetFullPath(Model);
            static bool Has(string f, string p) => System.IO.Path.GetFileName(f).IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0;
            var files = SearchFolders().SelectMany(d => Directory.GetFiles(d, "*.fbx"))
                .Where(f => !string.Equals(System.IO.Path.GetFullPath(f), model, StringComparison.OrdinalIgnoreCase))
                .Where(f => part switch
                {
                    "CrouchWalk" => Has(f, "Crouch") && Has(f, "Walk"),
                    "Crouch" => Has(f, "Crouch") && !Has(f, "Walk"),
                    "Walking" or "Walk" => Has(f, part) && !Has(f, "Crouch"),
                    _ => Has(f, part),
                }).ToList();
            var stem = System.IO.Path.GetFileNameWithoutExtension(Model);
            bool IsCrouch(string f) => System.IO.Path.GetFileName(f).IndexOf("Crouch", StringComparison.OrdinalIgnoreCase) >= 0;
            return files
                .OrderBy(f => part == "Idle" && IsCrouch(f) ? 1 : 0) // "Crouching Idle" is the crouch animation, not the idle one
                .ThenBy(f => System.IO.Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase) ? 0 : 1) // the model's own (Character_04@Idle.fbx)
                .FirstOrDefault();
        }

        public string FindTexture()
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(Model);
            foreach (var d in SearchFolders())
                foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tga" })
                {
                    var f = System.IO.Path.Combine(d, stem + ext);
                    if (File.Exists(f)) return f;
                }
            return null;
        }
    }

    /// <summary>
    /// FBX → .gmdl, the same file the Unity script (tools\ModelExport.cs) wrote: bone hierarchy, skinned mesh in the
    /// model's space, bind poses, texture (RGBA, at most 512 px) and the animations sampled at 30 frames per second.
    /// All values are in Unity's space: metres, Y up, left handed (X mirrored), the model facing +Z.
    /// </summary>
    static class Exporter
    {
        const float Fps = 30f;

        sealed class OutNode { public string Name; public int Parent; public Vec3 P; public Quat R; public Vec3 S; public SceneNode Src; public Mat4 WorldU; }

        public static string Export(ExportJob job, Action<string> log)
        {
            if (string.IsNullOrEmpty(job.Model) || !File.Exists(job.Model)) throw new FileNotFoundException("Character FBX not found", job.Model);
            log($"Model: {job.Model}");
            var scene = new FbxScene(job.Model);
            scene.LoadAnimation();
            // like Unity: when the file has a take, the model's default pose is that take's first frame
            if (scene.HasAnimation) { var (st, _) = scene.StackSpan(); scene.Evaluate(st); }
            scene.ComputeWorld();
            log($"  {scene.Nodes.Count} nodes, unit scale {Num.F(scene.UnitScale)} cm{(scene.HasAnimation ? ", rest pose from the file's own take" : "")}");

            // ---- nodes (node 0 = the model root, then depth first like GetComponentsInChildren) ----
            var nodes = new List<OutNode> { new() { Name = System.IO.Path.GetFileNameWithoutExtension(job.Model), Parent = -1, R = Quat.Identity, S = new Vec3(1, 1, 1), WorldU = Mat4.Identity() } };
            var index = new Dictionary<SceneNode, int>();
            foreach (var n in scene.DepthFirst())
            {
                var on = new OutNode { Name = n.Name, Src = n, WorldU = scene.ToUnity(n.World) };
                on.Parent = n.Parent != null && index.TryGetValue(n.Parent, out var pi) ? pi : 0;
                var local = nodes[on.Parent].WorldU.Inverse() * on.WorldU;
                local.Decompose(out on.P, out on.R, out on.S);
                index[n] = nodes.Count;
                nodes.Add(on);
            }
            int hips = nodes.FindIndex(x => x.Name.EndsWith("Hips", StringComparison.OrdinalIgnoreCase));

            // ---- meshes ----
            var mesh = BuildMesh(scene, nodes, index, log);
            log($"  mesh: {mesh.Verts.Count} vertices, {mesh.Subs.Sum(s => s.Count) / 3} triangles, {mesh.Bones.Count} bones");

            // ---- texture ----
            var (tw, th, rgba, texInfo) = LoadTexture(scene, job, log);
            log($"  texture: {texInfo} → {tw}x{th}");

            // ---- animations ----
            var clips = new List<(string name, List<Quat[]> rot, List<Vec3> hips)>();
            float animHips = job.AnimHipsOverride ?? 0f;
            foreach (var (key, file) in new[] { ("idle", job.Idle), ("walk", job.Walk), ("crouch", job.Crouch), ("crouchwalk", job.CrouchWalk) })
            {
                var path = file;
                bool builtIn = false;
                if (string.IsNullOrEmpty(path) && job.BuiltInAnimations) { path = DefaultAnims.PathOf(key); builtIn = path != null; }
                if (string.IsNullOrEmpty(path)) { log(key == "crouchwalk" ? "  crouchwalk: none – the mod makes it from walk + crouch" : $"  {key}: no animation"); continue; }
                if (hips < 0) { log($"  {key}: skipped – the model has no Hips bone"); continue; }
                string what = builtIn ? $"built-in \"{DefaultAnims.Title(key)}\"" : System.IO.Path.GetFileName(path);
                try
                {
                    var clip = SampleClip(path, nodes, hips, ref animHips, log);
                    if (clip.rot.Count > 0) { clips.Add((key, clip.rot, clip.hips)); log($"  {key}: {what} – {clip.rot.Count} frames, {clip.matched} bones animated"); }
                    else log($"  {key}: {what} – no bone of the model is animated (another skeleton?)");
                }
                catch (Exception e) { log($"  {key}: {what} could not be read: {e.Message}"); }
            }

            // ---- write ----
            var outPath = job.Output;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath)));
            using (var w = new BinaryWriter(File.Create(outPath)))
            {
                w.Write(0x4C444D47); w.Write(1); // "GMDL" v1
                w.Write(nodes.Count);
                foreach (var n in nodes) { w.Write(n.Name); w.Write(n.Parent); W(w, n.P); W(w, n.R); W(w, n.S); }
                w.Write(0); // the mesh is in the model root's space
                w.Write(hips >= 0 ? hips : 0);
                w.Write(mesh.Bones.Count); foreach (var b in mesh.Bones) w.Write(b);
                foreach (var bp in mesh.BindPoses) for (int k = 0; k < 16; k++) w.Write((float)bp[k % 4, k / 4]); // Unity's column-major order
                w.Write(mesh.Verts.Count);
                foreach (var v in mesh.Verts)
                {
                    W(w, v.P); W(w, v.N); w.Write((float)v.U); w.Write((float)v.V);
                    for (int k = 0; k < 4; k++) w.Write(v.Bi[k]);
                    for (int k = 0; k < 4; k++) w.Write(v.Bw[k]);
                }
                w.Write(mesh.Subs.Count);
                foreach (var s in mesh.Subs) { w.Write(s.Count); foreach (var t in s) w.Write(t); }
                w.Write(tw); w.Write(th); w.Write(rgba);
                w.Write(hips >= 0 ? clips.Count : 0);
                if (hips >= 0)
                    foreach (var c in clips)
                    {
                        w.Write(c.name); w.Write(Fps); w.Write(c.rot.Count);
                        for (int f = 0; f < c.rot.Count; f++) { foreach (var q in c.rot[f]) W(w, q); W(w, c.hips[f]); }
                    }
            }
            var sb = new StringBuilder();
            sb.Append($"Saved {outPath}: {nodes.Count} nodes, {mesh.Verts.Count} vertices, {mesh.Bones.Count} bones, texture {tw}x{th}, ");
            sb.Append(clips.Count == 0 ? "no animations" : "animations: " + string.Join(", ", clips.Select(c => c.name)));
            if (hips < 0) sb.Append(" (no Hips bone – the model stands still)");
            return sb.ToString();
        }

        static void W(BinaryWriter w, Vec3 v) { w.Write((float)v.X); w.Write((float)v.Y); w.Write((float)v.Z); }
        static void W(BinaryWriter w, Quat q) { w.Write((float)q.X); w.Write((float)q.Y); w.Write((float)q.Z); w.Write((float)q.W); }

        // ================= mesh =================
        sealed class OutVert { public Vec3 P, N; public double U, V; public int[] Bi = new int[4]; public float[] Bw = new float[4]; }
        sealed class OutMesh { public List<OutVert> Verts = new(); public List<List<int>> Subs = new(); public List<int> Bones = new(); public List<Mat4> BindPoses = new(); }

        static OutMesh BuildMesh(FbxScene scene, List<OutNode> nodes, Dictionary<SceneNode, int> index, Action<string> log)
        {
            var mesh = new OutMesh();
            var boneSlot = new Dictionary<int, int>(); // node index → bone slot
            int Slot(int node, Mat4 bindPose)
            {
                if (boneSlot.TryGetValue(node, out var s)) return s;
                boneSlot[node] = s = mesh.Bones.Count;
                mesh.Bones.Add(node); mesh.BindPoses.Add(bindPose);
                return s;
            }

            int meshCount = 0;
            var noNormals = new List<(int firstVert, int firstSub)>();
            foreach (var (geoId, geo) in scene.Objects.Where(kv => kv.Value.Name == "Geometry" && FbxScene.SubType(kv.Value) == "Mesh").Select(kv => (kv.Key, kv.Value)))
            {
                var modelId = scene.ParentsOf(geoId).FirstOrDefault(p => scene.Nodes.ContainsKey(p));
                if (!scene.Nodes.TryGetValue(modelId, out var meshNode)) continue;
                var cps = geo.Child("Vertices")?.Doubles();
                var poly = geo.Child("PolygonVertexIndex")?.Ints();
                if (cps == null || poly == null || cps.Length < 3) continue;
                meshCount++;
                int nCp = cps.Length / 3;
                var geoM = meshNode.GeometryMatrix();

                // skin: clusters of the Skin deformer on this geometry
                var weights = new List<(int slot, float w)>[nCp];
                var skin = scene.ChildrenOf(geoId).Select(id => scene.Objects.TryGetValue(id, out var o) ? (id, o) : (id, null))
                    .FirstOrDefault(x => x.o != null && x.o.Name == "Deformer" && FbxScene.SubType(x.o) == "Skin");
                if (skin.o != null)
                {
                    foreach (var clId in scene.ChildrenOf(skin.id))
                    {
                        if (!scene.Objects.TryGetValue(clId, out var cl) || cl.Name != "Deformer" || FbxScene.SubType(cl) != "Cluster") continue;
                        var boneId = scene.ChildrenOf(clId).FirstOrDefault(c => scene.Nodes.ContainsKey(c));
                        if (!scene.Nodes.TryGetValue(boneId, out var bone) || !index.TryGetValue(bone, out var bi)) continue;
                        // the bone where the mesh was bound to it (the cluster's own "Transform" is not used – like
                        // Unity, the mesh is where its node is; some exporters write something else there)
                        var tl = cl.Child("TransformLink")?.Doubles();
                        var linkW = tl != null && tl.Length == 16 ? Mat4.FromFbx(tl) : bone.World;
                        int slot = Slot(bi, scene.ToUnity(linkW).Inverse());
                        var ids = cl.Child("Indexes")?.Ints(); var ws = cl.Child("Weights")?.Doubles();
                        if (ids == null || ws == null) continue;
                        for (int k = 0; k < ids.Length && k < ws.Length; k++)
                        {
                            int cp = ids[k];
                            if ((uint)cp >= (uint)nCp || ws[k] <= 0) continue;
                            (weights[cp] ??= new()).Add((slot, (float)ws[k]));
                        }
                    }
                }
                // to the model root's space, where the mesh's node stands
                var toRoot = scene.A * meshNode.World * geoM;
                var normalM = toRoot.Inverse(); // normals: inverse transpose of the linear part
                int ownSlot = -1;
                int OwnSlot() => ownSlot >= 0 ? ownSlot : ownSlot = Slot(index[meshNode], scene.ToUnity(meshNode.World).Inverse());
                if (skin.o == null) log($"  mesh '{meshNode.Name}' has no skin – it follows its own node");

                // layers
                var normals = Layer(geo, "LayerElementNormal", "Normals", "NormalsIndex", 3);
                if (!normals.Has) { log($"  mesh '{meshNode.Name}' has no normals – smooth ones are computed"); noNormals.Add((mesh.Verts.Count, mesh.Subs.Count)); }
                var uvs = Layer(geo, "LayerElementUV", "UV", "UVIndex", 2);
                var mats = geo.Child("LayerElementMaterial");
                var matMap = mats?.Child("MappingInformationType")?.Str(0) ?? "AllSame";
                var matIdx = mats?.Child("Materials")?.Ints() ?? new[] { 0 };

                var dedupe = new Dictionary<(int, long, long, long, long, long), int>();
                var subOf = new Dictionary<int, List<int>>();
                int pv = 0, polyIndex = 0;
                var corners = new List<int>();
                for (int i = 0; i < poly.Length; i++)
                {
                    int raw = poly[i];
                    bool last = raw < 0;
                    int cp = last ? ~raw : raw;
                    corners.Add(MakeVert(cp, pv));
                    pv++;
                    if (!last) continue;
                    int mat = matMap == "ByPolygon" && polyIndex < matIdx.Length ? matIdx[polyIndex] : matIdx.Length > 0 ? matIdx[0] : 0;
                    if (!subOf.TryGetValue(mat, out var tris)) subOf[mat] = tris = new List<int>();
                    // polygons as a fan from their first corner (like Unity's import)
                    for (int k = 1; k + 1 < corners.Count; k++)
                    {
                        // mirrored into Unity's left-handed space: the winding is reversed
                        if (scene.Mirrored) { tris.Add(corners[0]); tris.Add(corners[k + 1]); tris.Add(corners[k]); }
                        else { tris.Add(corners[0]); tris.Add(corners[k]); tris.Add(corners[k + 1]); }
                    }
                    corners = new List<int>(); polyIndex++;
                }
                foreach (var kv in subOf.OrderBy(k => k.Key)) mesh.Subs.Add(kv.Value);

                int MakeVert(int cp, int polyVert)
                {
                    var n = normals.Get(cp, polyVert, polyIndex);
                    var uv = uvs.Get(cp, polyVert, polyIndex);
                    var key = (cp, Q(n[0]), Q(n[1]), Q(n[2]), Q(uv[0]), Q(uv[1]));
                    if (dedupe.TryGetValue(key, out var vi)) return vi;
                    var v = new OutVert
                    {
                        P = toRoot.Point(new Vec3(cps[cp * 3], cps[cp * 3 + 1], cps[cp * 3 + 2])),
                        N = normals.Has ? new Vec3(
                            normalM[0, 0] * n[0] + normalM[1, 0] * n[1] + normalM[2, 0] * n[2],
                            normalM[0, 1] * n[0] + normalM[1, 1] * n[1] + normalM[2, 1] * n[2],
                            normalM[0, 2] * n[0] + normalM[1, 2] * n[1] + normalM[2, 2] * n[2]).Normalized : new Vec3(0, 1, 0),
                        U = uv[0], V = uv[1],
                    };
                    var ws = weights[cp];
                    if (ws == null || ws.Count == 0) { v.Bi[0] = OwnSlot(); v.Bw[0] = 1f; }
                    else
                    {
                        // like Unity: the four strongest influences, normalised
                        var top = ws.GroupBy(x => x.slot).Select(g => (slot: g.Key, w: g.Sum(x => x.w))).OrderByDescending(x => x.w).Take(4).ToList();
                        float sum = top.Sum(x => x.w);
                        for (int k = 0; k < top.Count; k++) { v.Bi[k] = top[k].slot; v.Bw[k] = sum > 0 ? top[k].w / sum : 0; }
                    }
                    vi = mesh.Verts.Count;
                    mesh.Verts.Add(v);
                    dedupe[key] = vi;
                    return vi;
                }
            }
            if (meshCount == 0) throw new InvalidDataException("The FBX has no mesh");
            if (meshCount > 1) log($"  {meshCount} meshes merged into one");
            if (mesh.Verts.Count == 0) throw new InvalidDataException("The mesh has no vertices");
            if (noNormals.Count > 0) SmoothNormals(mesh, noNormals);
            return mesh;
        }

        static long Q(double v) => (long)Math.Round(v * 100000);

        /// <summary>Normals averaged from the faces around each vertex (meshes stored without normals).</summary>
        static void SmoothNormals(OutMesh mesh, List<(int firstVert, int firstSub)> parts)
        {
            var acc = new Vec3[mesh.Verts.Count];
            foreach (var (fv, fs) in parts)
                for (int s = fs; s < mesh.Subs.Count; s++)
                {
                    var t = mesh.Subs[s];
                    for (int i = 0; i + 2 < t.Count; i += 3)
                    {
                        var a = mesh.Verts[t[i]].P; var b = mesh.Verts[t[i + 1]].P; var c = mesh.Verts[t[i + 2]].P;
                        var n = Vec3.Cross(b - a, c - a); // (Unity: clockwise front faces in a left-handed space)
                        acc[t[i]] = acc[t[i]] + n; acc[t[i + 1]] = acc[t[i + 1]] + n; acc[t[i + 2]] = acc[t[i + 2]] + n;
                    }
                }
            // vertices split by their UVs share the normal of their position
            var byPos = new Dictionary<(long, long, long), Vec3>();
            for (int i = 0; i < acc.Length; i++)
            {
                if (acc[i].Length == 0) continue;
                var p = mesh.Verts[i].P; var k = (Q(p.X), Q(p.Y), Q(p.Z));
                byPos[k] = byPos.TryGetValue(k, out var e) ? e + acc[i] : acc[i];
            }
            foreach (var (fv, _) in parts)
                for (int i = fv; i < mesh.Verts.Count; i++)
                {
                    var p = mesh.Verts[i].P;
                    if (byPos.TryGetValue((Q(p.X), Q(p.Y), Q(p.Z)), out var n)) mesh.Verts[i].N = n.Normalized;
                }
        }

        /// <summary>A geometry layer (normals, UVs) read by control point / polygon vertex / polygon, direct or indexed.</summary>
        sealed class LayerData
        {
            public bool Has; public string Map = "", Ref = ""; public double[] Data; public int[] Index; public int Dim;
            public double[] Get(int cp, int polyVert, int poly)
            {
                var r = new double[Dim];
                if (!Has) return r;
                int i = Map switch
                {
                    "ByPolygonVertex" => polyVert,
                    "ByVertice" or "ByVertex" or "ByControlPoint" => cp,
                    "ByPolygon" => poly,
                    _ => 0, // AllSame
                };
                if (Ref == "IndexToDirect" || Ref == "Index") i = Index != null && i < Index.Length ? Index[i] : i;
                if (i < 0 || (i + 1) * Dim > Data.Length) return r;
                for (int k = 0; k < Dim; k++) r[k] = Data[i * Dim + k];
                return r;
            }
        }

        static LayerData Layer(FbxNode geo, string layer, string data, string indexName, int dim)
        {
            var l = geo.All(layer).FirstOrDefault();
            var d = new LayerData { Dim = dim };
            if (l == null) return d;
            d.Data = l.Child(data)?.Doubles();
            if (d.Data == null || d.Data.Length < dim) return d;
            d.Index = l.Child(indexName)?.Ints();
            d.Map = l.Child("MappingInformationType")?.Str(0) ?? "ByPolygonVertex";
            d.Ref = l.Child("ReferenceInformationType")?.Str(0) ?? "Direct";
            d.Has = true;
            return d;
        }

        // ================= animations =================
        static (List<Quat[]> rot, List<Vec3> hips, int matched) SampleClip(string file, List<OutNode> nodes, int hips, ref float animHips, Action<string> log)
        {
            var anim = new FbxScene(file);
            anim.LoadAnimation();
            if (!anim.HasAnimation) throw new InvalidDataException("no animation in the file");
            var (start, stop) = anim.StackSpan();
            double len = Math.Max(0, (stop - start) / (double)FbxScene.TicksPerSecond);
            int frames = Math.Max(2, (int)Math.Round(len * Fps) + 1);
            // the animation's bones by name (also without a "mixamorig:" style prefix)
            var byName = new Dictionary<string, SceneNode>(StringComparer.Ordinal);
            var byShort = new Dictionary<string, SceneNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in anim.Nodes.Values) { byName.TryAdd(n.Name, n); byShort.TryAdd(Short(n.Name), n); }
            var map = new SceneNode[nodes.Count];
            int matched = 0;
            for (int i = 1; i < nodes.Count; i++)
            {
                if (!byName.TryGetValue(nodes[i].Name, out var a)) byShort.TryGetValue(Short(nodes[i].Name), out a);
                if (a != null && a.Curves.ContainsKey("R")) { map[i] = a; matched++; }
            }
            if (map[hips] == null) log($"    (the animation has no curves for {nodes[hips].Name})");
            // the hips height this animation was made for: its own hips at the first frame
            anim.Evaluate(start);
            var animHipsNode = map[hips] ?? anim.Nodes.Values.FirstOrDefault(n => n.Name.EndsWith("Hips", StringComparison.OrdinalIgnoreCase));
            if (animHips <= 0 && animHipsNode != null)
            {
                anim.ToUnity(animHipsNode.LocalMatrix()).Decompose(out var hp0, out _, out _);
                animHips = (float)Math.Max(0.01, hp0.Y);
            }
            if (animHips <= 0) animHips = (float)Math.Max(0.01, nodes[hips].P.Y);
            double hipsScale = nodes[hips].P.Y / animHips;

            var rots = new List<Quat[]>(); var hipsPos = new List<Vec3>();
            for (int f = 0; f < frames; f++)
            {
                long t = start + (long)Math.Round(Math.Min(len, f / Fps) * FbxScene.TicksPerSecond);
                anim.Evaluate(t);
                var q = new Quat[nodes.Count];
                for (int i = 0; i < nodes.Count; i++)
                {
                    q[i] = nodes[i].R;
                    if (map[i] == null) continue;
                    anim.ToUnity(map[i].LocalMatrix()).Decompose(out var p, out var r, out _);
                    q[i] = r;
                    // walking in place: no forward drift, the height scaled to this model's legs
                    if (i == hips && map[i].Curves.ContainsKey("T")) hipsPos.Add(new Vec3(nodes[hips].P.X, p.Y * hipsScale, nodes[hips].P.Z));
                }
                if (hipsPos.Count < rots.Count + 1) hipsPos.Add(nodes[hips].P);
                rots.Add(q);
            }
            return (rots, hipsPos, matched);
        }

        static string Short(string n) { int i = n.LastIndexOf(':'); return i >= 0 ? n[(i + 1)..] : n; }

        // ================= texture =================
        static (int w, int h, byte[] rgba, string info) LoadTexture(FbxScene scene, ExportJob job, Action<string> log)
        {
            Bitmap bmp = null; string info = null;
            try
            {
                if (!string.IsNullOrEmpty(job.Texture) && File.Exists(job.Texture)) { bmp = LoadImage(File.ReadAllBytes(job.Texture)); info = System.IO.Path.GetFileName(job.Texture); }
                if (bmp == null)
                {
                    // a texture packed into the FBX (Video with Content), or the file it names when it exists
                    foreach (var v in scene.Objects.Values.Where(o => o.Name == "Video"))
                    {
                        if (v.Child("Content")?.Props.FirstOrDefault() is byte[] content && content.Length > 16)
                        {
                            bmp = LoadImage(content);
                            if (bmp != null) { info = "embedded in the FBX"; break; }
                        }
                        foreach (var key in new[] { "RelativeFilename", "Filename" })
                        {
                            var f = v.Child(key)?.Str(0);
                            if (string.IsNullOrEmpty(f)) continue;
                            var full = System.IO.Path.IsPathRooted(f) ? f : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(job.Model) ?? ".", f);
                            if (File.Exists(full)) { bmp = LoadImage(File.ReadAllBytes(full)); if (bmp != null) { info = full; break; } }
                        }
                        if (bmp != null) break;
                    }
                }
            }
            catch (Exception e) { log("  texture: " + e.Message); bmp = null; }
            if (bmp == null)
            {
                log("  texture: none found – the model will be grey (choose a PNG / JPG)");
                var grey = new byte[2 * 2 * 4];
                for (int i = 0; i < 4; i++) { grey[i * 4] = grey[i * 4 + 1] = grey[i * 4 + 2] = 128; grey[i * 4 + 3] = 255; }
                return (2, 2, grey, "none");
            }
            using (bmp)
            {
                int sw = bmp.Width, sh = bmp.Height;
                int tw = Math.Min(512, sw), th = Math.Min(512, sh);
                var src = new byte[sw * sh * 4];
                var bd = bmp.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                for (int y = 0; y < sh; y++) System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, src, y * sw * 4, sw * 4);
                bmp.UnlockBits(bd);
                // RGBA rows bottom to top, sampled bilinearly at the pixel centres (as Unity's GetPixelBilinear did)
                var o = new byte[tw * th * 4];
                for (int y = 0; y < th; y++)
                    for (int x = 0; x < tw; x++)
                    {
                        double u = (x + 0.5) / tw * sw - 0.5, v = (y + 0.5) / th * sh - 0.5;
                        int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
                        double fx = u - x0, fy = v - y0;
                        for (int ch = 0; ch < 4; ch++)
                        {
                            double c00 = Px(src, sw, sh, x0, y0, ch), c10 = Px(src, sw, sh, x0 + 1, y0, ch), c01 = Px(src, sw, sh, x0, y0 + 1, ch), c11 = Px(src, sw, sh, x0 + 1, y0 + 1, ch);
                            double c = (c00 * (1 - fx) + c10 * fx) * (1 - fy) + (c01 * (1 - fx) + c11 * fx) * fy;
                            o[(y * tw + x) * 4 + ch] = (byte)Math.Clamp(Math.Round(c), 0, 255);
                        }
                    }
                return (tw, th, o, $"{info} ({sw}x{sh})");
            }
        }

        /// <summary>Channel <paramref name="ch"/> (0 R, 1 G, 2 B, 3 A) of the pixel at (x, y) counted from the bottom row, repeating.</summary>
        static double Px(byte[] bgra, int w, int h, int x, int y, int ch)
        {
            x = ((x % w) + w) % w; y = ((y % h) + h) % h;
            int row = h - 1 - y; // bitmap rows start at the top
            int i = (row * w + x) * 4;
            return ch switch { 0 => bgra[i + 2], 1 => bgra[i + 1], 2 => bgra[i], _ => bgra[i + 3] };
        }

        static Bitmap LoadImage(byte[] data)
        {
            try { using var ms = new MemoryStream(data); using var img = Image.FromStream(ms, false, false); return new Bitmap(img); } // no colour management: the pixels as stored, like Unity
            catch { return Tga.Decode(data); }
        }
    }
}
