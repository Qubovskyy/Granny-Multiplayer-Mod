using System.Globalization;
using System.Text;

namespace GrannyModelTool
{
    /// <summary>Reads a .gmdl back (the same way the mod does) – for the summary after an export and for checks.</summary>
    sealed class Gmdl
    {
        public string[] Names; public int[] Parent; public float[][] P, R, S;
        public int SmrNode, RootBone; public int[] Bones; public float[][] Bind;
        public float[][] V, N, UV; public int[][] BI; public float[][] BW; public int[][] Tris;
        public int TexW, TexH; public byte[] Tex;
        public readonly List<(string name, float fps, int frames, float[][] rot, float[][] hips)> Clips = new();
        public float Height;

        public static Gmdl Read(string file)
        {
            using var r = new BinaryReader(File.OpenRead(file));
            if (r.ReadInt32() != 0x4C444D47) throw new InvalidDataException("not a GMDL file");
            if (r.ReadInt32() != 1) throw new InvalidDataException("unknown version");
            var g = new Gmdl();
            int n = r.ReadInt32();
            g.Names = new string[n]; g.Parent = new int[n]; g.P = new float[n][]; g.R = new float[n][]; g.S = new float[n][];
            for (int i = 0; i < n; i++) { g.Names[i] = r.ReadString(); g.Parent[i] = r.ReadInt32(); g.P[i] = F(r, 3); g.R[i] = F(r, 4); g.S[i] = F(r, 3); }
            g.SmrNode = r.ReadInt32(); g.RootBone = r.ReadInt32();
            int nb = r.ReadInt32();
            g.Bones = new int[nb]; for (int i = 0; i < nb; i++) g.Bones[i] = r.ReadInt32();
            g.Bind = new float[nb][]; for (int i = 0; i < nb; i++) g.Bind[i] = F(r, 16);
            int nv = r.ReadInt32();
            g.V = new float[nv][]; g.N = new float[nv][]; g.UV = new float[nv][]; g.BI = new int[nv][]; g.BW = new float[nv][];
            for (int i = 0; i < nv; i++)
            {
                g.V[i] = F(r, 3); g.N[i] = F(r, 3); g.UV[i] = F(r, 2);
                g.BI[i] = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
                g.BW[i] = F(r, 4);
                g.Height = Math.Max(g.Height, g.V[i][1]);
            }
            int ns = r.ReadInt32();
            g.Tris = new int[ns][];
            for (int s = 0; s < ns; s++) { int c = r.ReadInt32(); g.Tris[s] = new int[c]; for (int k = 0; k < c; k++) g.Tris[s][k] = r.ReadInt32(); }
            g.TexW = r.ReadInt32(); g.TexH = r.ReadInt32(); g.Tex = r.ReadBytes(g.TexW * g.TexH * 4);
            int nc = r.ReadInt32();
            for (int c = 0; c < nc; c++)
            {
                string name = r.ReadString(); float fps = r.ReadSingle(); int frames = r.ReadInt32();
                var rot = new float[frames * n][]; var hips = new float[frames][];
                for (int f = 0; f < frames; f++) { for (int i = 0; i < n; i++) rot[f * n + i] = F(r, 4); hips[f] = F(r, 3); }
                g.Clips.Add((name, fps, frames, rot, hips));
            }
            return g;
        }

        static float[] F(BinaryReader r, int n) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = r.ReadSingle(); return a; }
    }

    static class GmdlInfo
    {
        static string V(float[] a) => "(" + string.Join(", ", a.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture))) + ")";

        public static string Describe(string file, bool full = false)
        {
            var g = Gmdl.Read(file);
            var sb = new StringBuilder();
            sb.AppendLine($"{Path.GetFileName(file)}: {g.Names.Length} nodes, {g.Bones.Length} bones, {g.V.Length} vertices, {g.Tris.Sum(t => t.Length) / 3} triangles in {g.Tris.Length} part(s), height {g.Height:0.###}, texture {g.TexW}x{g.TexH}");
            sb.AppendLine("animations: " + (g.Clips.Count == 0 ? "none" : string.Join(", ", g.Clips.Select(c => $"{c.name} ({c.frames} frames, {(c.frames - 1) / c.fps:0.##} s)"))));
            if (!full) return sb.ToString();
            for (int i = 0; i < g.Names.Length; i++) sb.AppendLine($"  node {i} {g.Names[i]} parent {g.Parent[i]} P{V(g.P[i])} R{V(g.R[i])} S{V(g.S[i])}");
            for (int b = 0; b < g.Bones.Length; b++) sb.AppendLine($"  bone {b} -> node {g.Bones[b]} {g.Names[g.Bones[b]]} bind {V(g.Bind[b])}");
            foreach (var c in g.Clips)
                foreach (int f in new[] { 0, c.frames / 2 })
                {
                    sb.AppendLine($"  {c.name} frame {f}: hips {V(c.hips[f])}");
                    for (int i = 0; i < g.Names.Length; i++) sb.AppendLine($"    {g.Names[i]} {V(c.rot[f * g.Names.Length + i])}");
                }
            return sb.ToString();
        }
    }
}
