using System.Globalization;

namespace GrannyModelTool
{
    /// <summary>An FBX scene: objects, connections, the node hierarchy with transforms, meshes and animation curves.</summary>
    sealed class FbxScene
    {
        public const long TicksPerSecond = 46186158000L;

        public readonly string Path;
        public readonly FbxNode Doc;
        public readonly Dictionary<long, FbxNode> Objects = new();
        /// <summary>child → parents (object-object connections) and child → (parent, property) for property connections.</summary>
        readonly Dictionary<long, List<long>> _parents = new();
        readonly Dictionary<long, List<long>> _children = new();
        readonly Dictionary<long, List<(long child, string prop)>> _propChildren = new();
        readonly Dictionary<long, List<(long parent, string prop)>> _propParents = new();

        public readonly SceneNode Root = new() { Id = 0, Name = "Root" };
        public readonly Dictionary<long, SceneNode> Nodes = new();
        /// <summary>File space → Unity space for points (axes to Y-up, units to metres, X mirrored).</summary>
        public Mat4 A, AInv;
        public double UnitScale = 1;
        public bool HasAnimation => FirstStack() != null;

        public FbxScene(string path)
        {
            Path = path;
            Doc = FbxReader.Load(path);
            var objs = Doc.Child("Objects") ?? throw new InvalidDataException("No Objects section – not an FBX scene file");
            foreach (var o in objs.Children)
                if (o.Props.Count > 0 && o.Props[0] is long id) Objects[id] = o;
            var conns = Doc.Child("Connections");
            if (conns != null)
                foreach (var c in conns.All("C"))
                {
                    if (c.Props.Count < 3) continue;
                    string kind = c.Str(0);
                    long child = c.Long(1), parent = c.Long(2);
                    if (kind == "OO")
                    {
                        Add(_parents, child, parent); Add(_children, parent, child);
                    }
                    else if (kind == "OP" && c.Props.Count > 3)
                    {
                        string prop = c.Str(3);
                        if (!_propChildren.TryGetValue(parent, out var l)) _propChildren[parent] = l = new();
                        l.Add((child, prop));
                        if (!_propParents.TryGetValue(child, out var l2)) _propParents[child] = l2 = new();
                        l2.Add((parent, prop));
                    }
                }
            SetupAxes();
            BuildHierarchy();
        }

        static void Add(Dictionary<long, List<long>> d, long k, long v) { if (!d.TryGetValue(k, out var l)) d[k] = l = new(); l.Add(v); }

        public IEnumerable<long> ChildrenOf(long id) => _children.TryGetValue(id, out var l) ? l : Enumerable.Empty<long>();
        public IEnumerable<long> ParentsOf(long id) => _parents.TryGetValue(id, out var l) ? l : Enumerable.Empty<long>();
        public IEnumerable<(long child, string prop)> PropChildrenOf(long id) => _propChildren.TryGetValue(id, out var l) ? l : Enumerable.Empty<(long, string)>();
        public IEnumerable<(long parent, string prop)> PropParentsOf(long id) => _propParents.TryGetValue(id, out var l) ? l : Enumerable.Empty<(long, string)>();

        /// <summary>"Name\0\x01Class" (binary) or "Class::Name" (ASCII) → "Name".</summary>
        public static string CleanName(string s)
        {
            if (s == null) return "";
            int z = s.IndexOf('\0');
            if (z >= 0) return s[..z];
            int c = s.IndexOf("::", StringComparison.Ordinal);
            return c >= 0 ? s[(c + 2)..] : s;
        }

        public static string NameOf(FbxNode o) => o.Props.Count > 1 ? CleanName(o.Str(1)) : "";
        public static string SubType(FbxNode o) => o.Props.Count > 2 ? o.Str(2) : "";

        /// <summary>A Properties70 value (P: name, type, label, flags, values…).</summary>
        public static FbxNode Prop(FbxNode o, string name)
        {
            var p70 = o?.Child("Properties70") ?? o?.Child("Properties60");
            return p70?.All("P").Concat(p70.All("Property")).FirstOrDefault(p => p.Props.Count > 0 && p.Str(0) == name);
        }

        public static Vec3? PropVec(FbxNode o, string name)
        {
            var p = Prop(o, name);
            if (p == null || p.Props.Count < 7) return null;
            return new Vec3(p.Double(4), p.Double(5), p.Double(6));
        }

        public static double? PropNum(FbxNode o, string name)
        {
            var p = Prop(o, name);
            if (p == null || p.Props.Count < 5) return null;
            try { return p.Double(4); } catch { return null; }
        }

        // ---------------- axes and units ----------------
        void SetupAxes()
        {
            var gs = Doc.Child("GlobalSettings");
            int up = (int)(PropNum(gs, "UpAxis") ?? 1), upS = (int)(PropNum(gs, "UpAxisSign") ?? 1);
            int front = (int)(PropNum(gs, "FrontAxis") ?? 2), frontS = (int)(PropNum(gs, "FrontAxisSign") ?? 1);
            int coord = (int)(PropNum(gs, "CoordAxis") ?? 0), coordS = (int)(PropNum(gs, "CoordAxisSign") ?? 1);
            UnitScale = PropNum(gs, "UnitScaleFactor") ?? 1;
            if (UnitScale <= 0) UnitScale = 1;
            // canonical (Y up, Z front, X right – right handed) from the file's axes
            var c = new Mat4();
            c[0, coord] = coordS; c[1, up] = upS; c[2, front] = frontS; c[3, 3] = 1;
            if (new[] { up, front, coord }.Distinct().Count() != 3) c = Mat4.Identity();
            // Unity: centimetres → metres (file units are UnitScaleFactor cm), left handed (X mirrored)
            double s = UnitScale / 100.0;
            A = Mat4.Diag(-s, s, s) * c;
            AInv = A.Inverse();
        }

        /// <summary>A file-space matrix (local or world) in Unity space.</summary>
        public Mat4 ToUnity(Mat4 m) => A * m * AInv;
        public bool Mirrored => Det3(A) < 0;
        static double Det3(Mat4 m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

        // ---------------- hierarchy ----------------
        void BuildHierarchy()
        {
            foreach (var kv in Objects)
            {
                if (kv.Value.Name != "Model") continue;
                var n = new SceneNode { Id = kv.Key, Name = NameOf(kv.Value), Type = SubType(kv.Value), Obj = kv.Value };
                n.ReadProps();
                Nodes[kv.Key] = n;
            }
            foreach (var n in Nodes.Values)
            {
                var parentId = ParentsOf(n.Id).FirstOrDefault(p => p == 0 || Nodes.ContainsKey(p));
                n.Parent = Nodes.TryGetValue(parentId, out var p) ? p : Root;
                n.Parent.Children.Add(n);
            }
            // children in file order (Unity keeps the file's order)
            SortChildren(Root);
        }

        void SortChildren(SceneNode n)
        {
            var order = ChildrenOf(n.Id).ToList();
            n.Children.Sort((a, b) => order.IndexOf(a.Id).CompareTo(order.IndexOf(b.Id)));
            foreach (var c in n.Children) SortChildren(c);
        }

        public IEnumerable<SceneNode> DepthFirst()
        {
            var stack = new Stack<SceneNode>();
            for (int i = Root.Children.Count - 1; i >= 0; i--) stack.Push(Root.Children[i]);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
        }

        // ---------------- animation ----------------
        public FbxNode FirstStack() => Objects.Values.FirstOrDefault(o => o.Name == "AnimationStack");

        /// <summary>The time span of the first take (seconds, in file ticks).</summary>
        public (long start, long stop) StackSpan()
        {
            var st = FirstStack();
            long start = 0, stop = 0;
            var ls = Prop(st, "LocalStart"); if (ls != null && ls.Props.Count > 4) start = ls.Long(4);
            var le = Prop(st, "LocalStop"); if (le != null && le.Props.Count > 4) stop = le.Long(4);
            if (stop <= start)
            {
                // no span stored – the keys decide
                long min = long.MaxValue, max = long.MinValue;
                foreach (var n in Nodes.Values)
                    foreach (var ch in n.Curves.Values)
                        foreach (var cv in ch) if (cv != null && cv.Times.Length > 0) { min = Math.Min(min, cv.Times[0]); max = Math.Max(max, cv.Times[^1]); }
                if (min <= max) { start = min; stop = max; }
            }
            return (start, stop);
        }

        /// <summary>Connects the animation curves of the first take to the nodes (T / R / S, X / Y / Z).</summary>
        public void LoadAnimation()
        {
            var stack = FirstStack();
            if (stack == null) return;
            long stackId = stack.Long(0);
            var layers = ChildrenOf(stackId).Where(id => Objects.TryGetValue(id, out var o) && o.Name == "AnimationLayer").ToList();
            if (layers.Count == 0) return;
            long layer = layers[0];
            foreach (var cnId in ChildrenOf(layer))
            {
                if (!Objects.TryGetValue(cnId, out var cn) || cn.Name != "AnimationCurveNode") continue;
                foreach (var (parent, prop) in PropParentsOf(cnId))
                {
                    if (!Nodes.TryGetValue(parent, out var node)) continue;
                    string ch = prop switch { "Lcl Translation" => "T", "Lcl Rotation" => "R", "Lcl Scaling" => "S", _ => null };
                    if (ch == null) continue;
                    var arr = new Curve[3];
                    var defaults = new double?[3];
                    for (int a = 0; a < 3; a++) defaults[a] = PropNum(cn, "d|" + "XYZ"[a]);
                    foreach (var (curveId, cprop) in PropChildrenOf(cnId))
                    {
                        if (!Objects.TryGetValue(curveId, out var co) || co.Name != "AnimationCurve") continue;
                        int axis = cprop.EndsWith("X") ? 0 : cprop.EndsWith("Y") ? 1 : cprop.EndsWith("Z") ? 2 : -1;
                        if (axis >= 0) arr[axis] = Curve.Read(co);
                    }
                    node.Curves[ch] = arr;
                    node.CurveDefaults[ch] = defaults;
                }
            }
        }

        /// <summary>Puts every animated node at time <paramref name="ticks"/> (no curves = its own values).</summary>
        public void Evaluate(long ticks)
        {
            foreach (var n in Nodes.Values) n.Evaluate(ticks);
        }

        /// <summary>World matrices (file space) of the current pose.</summary>
        public void ComputeWorld()
        {
            foreach (var n in DepthFirst())
                n.World = (n.Parent == null || n.Parent == Root ? Mat4.Identity() : n.Parent.World) * n.LocalMatrix();
        }
    }

    sealed class SceneNode
    {
        public long Id; public string Name = "", Type = ""; public FbxNode Obj;
        public SceneNode Parent; public readonly List<SceneNode> Children = new();
        // static transform properties
        public Vec3 LclT, LclR, LclS = new(1, 1, 1);
        public Vec3 PreRot, PostRot, ROff, RPiv, SOff, SPiv, GeoT, GeoR, GeoS = new(1, 1, 1);
        public int RotOrder;
        // the pose now
        public Vec3 T, R, S = new(1, 1, 1);
        public Mat4 World = Mat4.Identity();
        public readonly Dictionary<string, Curve[]> Curves = new();
        public readonly Dictionary<string, double?[]> CurveDefaults = new();

        public void ReadProps()
        {
            LclT = FbxScene.PropVec(Obj, "Lcl Translation") ?? default;
            LclR = FbxScene.PropVec(Obj, "Lcl Rotation") ?? default;
            LclS = FbxScene.PropVec(Obj, "Lcl Scaling") ?? new Vec3(1, 1, 1);
            bool rotActive = (FbxScene.PropNum(Obj, "RotationActive") ?? 1) != 0;
            PreRot = FbxScene.PropVec(Obj, "PreRotation") ?? default;
            PostRot = FbxScene.PropVec(Obj, "PostRotation") ?? default;
            RotOrder = rotActive ? (int)(FbxScene.PropNum(Obj, "RotationOrder") ?? 0) : 0;
            ROff = FbxScene.PropVec(Obj, "RotationOffset") ?? default;
            RPiv = FbxScene.PropVec(Obj, "RotationPivot") ?? default;
            SOff = FbxScene.PropVec(Obj, "ScalingOffset") ?? default;
            SPiv = FbxScene.PropVec(Obj, "ScalingPivot") ?? default;
            GeoT = FbxScene.PropVec(Obj, "GeometricTranslation") ?? default;
            GeoR = FbxScene.PropVec(Obj, "GeometricRotation") ?? default;
            GeoS = FbxScene.PropVec(Obj, "GeometricScaling") ?? new Vec3(1, 1, 1);
            T = LclT; R = LclR; S = LclS;
        }

        public void Evaluate(long ticks)
        {
            T = Channel("T", LclT, ticks); R = Channel("R", LclR, ticks); S = Channel("S", LclS, ticks);
        }

        Vec3 Channel(string ch, Vec3 own, long ticks)
        {
            if (!Curves.TryGetValue(ch, out var c)) return own;
            var d = CurveDefaults[ch];
            double V(int a, double fallback) => c[a] != null && c[a].Times.Length > 0 ? c[a].Eval(ticks) : d[a] ?? fallback;
            return new Vec3(V(0, own.X), V(1, own.Y), V(2, own.Z));
        }

        /// <summary>FBX local matrix: T · Roff · Rp · Rpre · R · Rpost⁻¹ · Rp⁻¹ · Soff · Sp · S · Sp⁻¹.</summary>
        public Mat4 LocalMatrix()
        {
            var rp = Mat4.Translation(RPiv); var sp = Mat4.Translation(SPiv);
            return Mat4.Translation(T) * Mat4.Translation(ROff) * rp * Mat4.Euler(PreRot, 0) * Mat4.Euler(R, RotOrder) * Mat4.Euler(PostRot, 0).Inverse() * rp.Inverse()
                   * Mat4.Translation(SOff) * sp * Mat4.Scale(S) * sp.Inverse();
        }

        /// <summary>The mesh's geometry offset (not inherited by children).</summary>
        public Mat4 GeometryMatrix() => Mat4.Translation(GeoT) * Mat4.Euler(GeoR, 0) * Mat4.Scale(GeoS);

        public override string ToString() => $"{Name} ({Type})";
    }

    /// <summary>An FBX animation curve (key times in ticks, values; constant / linear / cubic keys evaluated linearly or held).</summary>
    sealed class Curve
    {
        public long[] Times = Array.Empty<long>();
        public float[] Values = Array.Empty<float>();
        public bool[] Constant = Array.Empty<bool>();

        public static Curve Read(FbxNode o)
        {
            var c = new Curve();
            var kt = o.Child("KeyTime"); var kv = o.Child("KeyValueFloat") ?? o.Child("KeyValue");
            if (kt == null || kv == null) return c;
            c.Times = kt.Longs();
            c.Values = kv.Doubles().Select(x => (float)x).ToArray();
            int n = Math.Min(c.Times.Length, c.Values.Length);
            if (c.Times.Length != n) c.Times = c.Times.Take(n).ToArray();
            if (c.Values.Length != n) c.Values = c.Values.Take(n).ToArray();
            // interpolation flags (shared by runs of keys: KeyAttrRefCount says how many keys use each flag)
            c.Constant = new bool[n];
            var flags = o.Child("KeyAttrFlags")?.Ints(); var refs = o.Child("KeyAttrRefCount")?.Ints();
            if (flags != null && refs != null)
            {
                int k = 0;
                for (int f = 0; f < flags.Length && f < refs.Length; f++)
                    for (int r = 0; r < refs[f] && k < n; r++, k++) c.Constant[k] = (flags[f] & 0x0E) == 0x02; // eInterpolationConstant
            }
            return c;
        }

        public double Eval(long t)
        {
            int n = Times.Length;
            if (n == 0) return 0;
            if (t <= Times[0]) return Values[0];
            if (t >= Times[n - 1]) return Values[n - 1];
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (Times[mid] <= t) lo = mid; else hi = mid; }
            if (Constant[lo]) return Values[lo];
            double f = (double)(t - Times[lo]) / (Times[hi] - Times[lo]);
            return Values[lo] + (Values[hi] - Values[lo]) * f;
        }
    }

    static class Num
    {
        public static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
