using System.Text;

namespace GrannyModelTool
{
    /// <summary>
    /// Command line:
    ///   GrannyModelTool model.fbx [--texture tex.png] [--idle a.fbx] [--walk b.fbx] [--crouch c.fbx] [--crouchwalk d.fbx] [--out file.gmdl]
    ///                             [--builtin-anims]  (ignore animation files next to the model)  [--no-builtin-anims]
    ///   GrannyModelTool --dump file.fbx      (prints the FBX tree)
    ///   GrannyModelTool --info file.gmdl     (prints a .gmdl summary)
    /// Animations not given are looked for next to the model (file names containing Idle / Walking / Crouch); the ones
    /// still missing use the tool's built-in animations (Mixamo Idle / Walking / Crouching Idle).
    /// </summary>
    static class Cli
    {
        public static int Run(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                if (args[0] == "--dump") { Dump(FbxReader.Load(args[1]), 0, args.Length > 2 ? int.Parse(args[2]) : 6); return 0; }
                if (args[0] == "--snap") { Application.EnableVisualStyles(); using var f = new MainForm(); f.Show(); Application.DoEvents(); using var b = new Bitmap(f.Width, f.Height); f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); b.Save(args[1]); return 0; }
                if (args[0] == "--info") { Console.WriteLine(GmdlInfo.Describe(args[1], args.Length > 2 && args[2] == "--full")); return 0; }
                var job = new ExportJob { Model = args[0] };
                bool search = true;
                for (int i = 1; i < args.Length; i++)
                {
                    string a = args[i], v = i + 1 < args.Length ? args[i + 1] : null;
                    switch (a)
                    {
                        case "--texture": job.Texture = v; i++; break;
                        case "--idle": job.Idle = v; i++; break;
                        case "--walk": job.Walk = v; i++; break;
                        case "--crouch": job.Crouch = v; i++; break;
                        case "--crouchwalk": job.CrouchWalk = v; i++; break;
                        case "--out": job.Output = v; i++; break;
                        case "--builtin-anims": search = false; break;
                        case "--no-builtin-anims": job.BuiltInAnimations = false; break;
                        case "--anim-hips": job.AnimHipsOverride = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); i++; break;
                        default: Console.Error.WriteLine("Unknown option " + a); return 2;
                    }
                }
                job.FillDefaults(search);
                var res = Exporter.Export(job, Console.WriteLine);
                Console.WriteLine(res);
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine("ERROR: " + e); return 1; }
        }

        static void Dump(FbxNode n, int depth, int maxDepth)
        {
            foreach (var c in n.Children)
            {
                var sb = new StringBuilder(new string(' ', depth * 2) + c.Name + ":");
                foreach (var p in c.Props.Take(8))
                {
                    sb.Append(' ');
                    sb.Append(p switch
                    {
                        string s => "\"" + s.Replace("\0\u0001", "::").Replace("\0", "\\0") + "\"",
                        Array arr => $"[{arr.GetType().GetElementType()?.Name} x{arr.Length}]",
                        _ => Convert.ToString(p, System.Globalization.CultureInfo.InvariantCulture),
                    });
                }
                if (c.Props.Count > 8) sb.Append(" ...");
                Console.WriteLine(sb);
                if (depth + 1 < maxDepth) Dump(c, depth + 1, maxDepth);
            }
        }
    }
}
