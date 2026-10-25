using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// A small PNG reader for the mod's own pictures (Unity's ImageConversion.LoadImage is stripped from this game):
    /// 8-bit grey / grey+alpha / RGB / RGBA / palette, not interlaced.
    /// </summary>
    internal static class Png
    {
        /// <summary>
        /// One of the mod's pictures: a file of that name next to the mod (to replace it), otherwise the copy built into
        /// GrannyMP.dll (assets/*.png are embedded resources – the mod is a single file).
        /// </summary>
        public static Texture2D Asset(string name)
        {
            try
            {
                var file = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", name);
                if (File.Exists(file)) { var t = Load(file); if (t != null) return t; }
                using var s = typeof(Plugin).Assembly.GetManifestResourceStream("GrannyMP.assets." + name);
                if (s == null) { Log.Debug("picture not found: " + name); return null; }
                var ms = new MemoryStream(); s.CopyTo(ms);
                var tex = Decode(ms.ToArray());
                if (tex != null) UnityEngine.Object.DontDestroyOnLoad(tex);
                return tex;
            }
            catch (Exception e) { Log.Debug($"picture {name}: {e.Message}"); return null; }
        }

        public static Texture2D Load(string file)
        {
            try { return File.Exists(file) ? Decode(File.ReadAllBytes(file)) : null; }
            catch (Exception e) { Log.Debug("png: " + e.Message); return null; }
        }

        static Texture2D Decode(byte[] d)
        {
            try
            {
                if (d.Length < 33 || d[0] != 0x89 || d[1] != 'P' || d[2] != 'N' || d[3] != 'G') return null;
                int w = 0, h = 0, depth = 0, type = 0, interlace = 0; byte[] pal = null, trns = null;
                var idat = new MemoryStream();
                for (int p = 8; p + 8 <= d.Length;)
                {
                    int len = (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
                    string kind = System.Text.Encoding.ASCII.GetString(d, p + 4, 4);
                    int at = p + 8;
                    if (kind == "IHDR") { w = Be(d, at); h = Be(d, at + 4); depth = d[at + 8]; type = d[at + 9]; interlace = d[at + 12]; }
                    else if (kind == "PLTE") { pal = new byte[len]; Array.Copy(d, at, pal, 0, len); }
                    else if (kind == "tRNS") { trns = new byte[len]; Array.Copy(d, at, trns, 0, len); }
                    else if (kind == "IDAT") idat.Write(d, at, len);
                    else if (kind == "IEND") break;
                    p = at + len + 4;
                }
                if (w <= 0 || h <= 0 || depth != 8 || interlace != 0) { Log.Warn("png: only 8-bit, not interlaced PNG files are read"); return null; }
                int ch = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
                if (ch == 0) return null;
                idat.Position = 0;
                byte[] raw;
                using (var z = new ZLibStream(idat, CompressionMode.Decompress)) using (var o = new MemoryStream()) { z.CopyTo(o); raw = o.ToArray(); }
                int stride = w * ch;
                var cur = new byte[stride]; var prev = new byte[stride];
                var px = new Color32[w * h];
                for (int y = 0, at = 0; y < h; y++)
                {
                    int filter = raw[at++];
                    for (int x = 0; x < stride; x++)
                    {
                        int a = x >= ch ? cur[x - ch] : 0, b = prev[x], c = x >= ch ? prev[x - ch] : 0, v = raw[at++];
                        cur[x] = (byte)(filter switch { 1 => v + a, 2 => v + b, 3 => v + ((a + b) >> 1), 4 => v + Paeth(a, b, c), _ => v });
                    }
                    int row = (h - 1 - y) * w; // Unity textures start at the bottom
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * ch;
                        px[row + x] = type switch
                        {
                            0 => new Color32(cur[i], cur[i], cur[i], 255),
                            4 => new Color32(cur[i], cur[i], cur[i], cur[i + 1]),
                            2 => new Color32(cur[i], cur[i + 1], cur[i + 2], 255),
                            3 => pal != null && cur[i] * 3 + 2 < pal.Length ? new Color32(pal[cur[i] * 3], pal[cur[i] * 3 + 1], pal[cur[i] * 3 + 2], trns != null && cur[i] < trns.Length ? trns[cur[i]] : (byte)255) : new Color32(0, 0, 0, 0),
                            _ => new Color32(cur[i], cur[i + 1], cur[i + 2], cur[i + 3]),
                        };
                    }
                    (prev, cur) = (cur, prev);
                }
                var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                t.SetPixels32(px);
                t.Apply();
                return t;
            }
            catch (Exception e) { Log.Warn("png: " + e.Message); return null; }
        }

        static int Be(byte[] d, int p) => (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }
}
