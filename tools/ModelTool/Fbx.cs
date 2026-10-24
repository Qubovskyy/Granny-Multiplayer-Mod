using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace GrannyModelTool
{
    /// <summary>One FBX record: a name, its values and child records (binary and ASCII files give the same tree).</summary>
    sealed class FbxNode
    {
        public string Name = "";
        public readonly List<object> Props = new();
        public readonly List<FbxNode> Children = new();

        public FbxNode Child(string name) => Children.FirstOrDefault(c => c.Name == name);
        public IEnumerable<FbxNode> All(string name) => Children.Where(c => c.Name == name);

        public long Long(int i) => Convert.ToInt64(Props[i], CultureInfo.InvariantCulture);
        public double Double(int i) => Convert.ToDouble(Props[i], CultureInfo.InvariantCulture);
        public string Str(int i) => i < Props.Count ? Props[i] as string ?? Convert.ToString(Props[i], CultureInfo.InvariantCulture) : "";

        /// <summary>The array value of this record (binary: one array property; ASCII: the "a" child).</summary>
        public double[] Doubles()
        {
            object v = Props.Count > 0 && Props[0] is Array ? Props[0] : Child("a")?.Props.ToArray();
            return v switch
            {
                double[] d => d,
                float[] f => f.Select(x => (double)x).ToArray(),
                long[] l => l.Select(x => (double)x).ToArray(),
                int[] n => n.Select(x => (double)x).ToArray(),
                object[] o => o.Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray(),
                _ => Props.Where(p => p is not string).Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray(),
            };
        }

        public long[] Longs()
        {
            object v = Props.Count > 0 && Props[0] is Array ? Props[0] : Child("a")?.Props.ToArray();
            return v switch
            {
                long[] l => l,
                int[] n => n.Select(x => (long)x).ToArray(),
                double[] d => d.Select(x => (long)x).ToArray(),
                float[] f => f.Select(x => (long)x).ToArray(),
                object[] o => o.Select(x => Convert.ToInt64(x, CultureInfo.InvariantCulture)).ToArray(),
                _ => Props.Where(p => p is not string).Select(x => Convert.ToInt64(x, CultureInfo.InvariantCulture)).ToArray(),
            };
        }

        public int[] Ints() => Longs().Select(x => (int)x).ToArray();
    }

    /// <summary>Reads binary (6.x / 7.x) and ASCII FBX files into a tree of <see cref="FbxNode"/>.</summary>
    static class FbxReader
    {
        const string BinaryMagic = "Kaydara FBX Binary  ";

        public static FbxNode Load(string path)
        {
            var data = File.ReadAllBytes(path);
            if (data.Length > 27 && Encoding.ASCII.GetString(data, 0, BinaryMagic.Length) == BinaryMagic) return ReadBinary(data);
            return ReadAscii(Encoding.UTF8.GetString(data));
        }

        // ---------------- binary ----------------
        static FbxNode ReadBinary(byte[] d)
        {
            int version = BitConverter.ToInt32(d, 23);
            bool wide = version >= 7500;
            long pos = 27;
            var root = new FbxNode();
            while (pos < d.Length)
            {
                var n = ReadNode(d, ref pos, wide);
                if (n == null) break;
                root.Children.Add(n);
            }
            root.Props.Add(version);
            return root;
        }

        static FbxNode ReadNode(byte[] d, ref long pos, bool wide)
        {
            long end, count;
            if (wide) { end = (long)BitConverter.ToUInt64(d, (int)pos); count = (long)BitConverter.ToUInt64(d, (int)pos + 8); pos += 24; }
            else { end = BitConverter.ToUInt32(d, (int)pos); count = BitConverter.ToUInt32(d, (int)pos + 4); pos += 12; }
            int nameLen = d[pos++];
            if (end == 0) return null; // the empty record that closes a list
            var node = new FbxNode { Name = Encoding.ASCII.GetString(d, (int)pos, nameLen) };
            pos += nameLen;
            for (long i = 0; i < count; i++) node.Props.Add(ReadProp(d, ref pos));
            while (pos < end)
            {
                var c = ReadNode(d, ref pos, wide);
                if (c == null) break;
                node.Children.Add(c);
            }
            pos = end;
            return node;
        }

        static object ReadProp(byte[] d, ref long p)
        {
            int i = (int)p;
            char t = (char)d[i];
            p++; i++;
            switch (t)
            {
                case 'Y': p += 2; return (long)BitConverter.ToInt16(d, i);
                case 'C': p += 1; return d[i] != 0;
                case 'I': p += 4; return (long)BitConverter.ToInt32(d, i);
                case 'F': p += 4; return (double)BitConverter.ToSingle(d, i);
                case 'D': p += 8; return BitConverter.ToDouble(d, i);
                case 'L': p += 8; return BitConverter.ToInt64(d, i);
                case 'S':
                {
                    int len = BitConverter.ToInt32(d, i); p += 4 + len;
                    return Encoding.UTF8.GetString(d, i + 4, len);
                }
                case 'R':
                {
                    int len = BitConverter.ToInt32(d, i); p += 4 + len;
                    var raw = new byte[len]; Array.Copy(d, i + 4, raw, 0, len);
                    return raw;
                }
                case 'f': case 'd': case 'l': case 'i': case 'b':
                {
                    int n = BitConverter.ToInt32(d, i), enc = BitConverter.ToInt32(d, i + 4), clen = BitConverter.ToInt32(d, i + 8);
                    p += 12 + clen;
                    byte[] raw;
                    if (enc == 1)
                    {
                        using var zs = new ZLibStream(new MemoryStream(d, i + 12, clen), CompressionMode.Decompress);
                        using var ms = new MemoryStream();
                        zs.CopyTo(ms);
                        raw = ms.ToArray();
                    }
                    else { raw = new byte[clen]; Array.Copy(d, i + 12, raw, 0, clen); }
                    switch (t)
                    {
                        case 'f': { var a = new float[n]; Buffer.BlockCopy(raw, 0, a, 0, n * 4); return a; }
                        case 'd': { var a = new double[n]; Buffer.BlockCopy(raw, 0, a, 0, n * 8); return a; }
                        case 'l': { var a = new long[n]; Buffer.BlockCopy(raw, 0, a, 0, n * 8); return a; }
                        case 'i': { var a = new int[n]; Buffer.BlockCopy(raw, 0, a, 0, n * 4); return a; }
                        default: { var a = new long[n]; for (int k = 0; k < n; k++) a[k] = raw[k]; return a; }
                    }
                }
                default: throw new InvalidDataException($"Unknown FBX property type '{t}' at {i - 1}");
            }
        }

        // ---------------- ASCII ----------------
        static FbxNode ReadAscii(string s)
        {
            var tok = new AsciiTokens(s);
            var root = new FbxNode();
            while (tok.Peek() != null) { var n = ReadAsciiNode(tok); if (n != null) root.Children.Add(n); }
            root.Props.Add(7400L);
            return root;
        }

        static FbxNode ReadAsciiNode(AsciiTokens tok)
        {
            var name = tok.Next();
            if (name == null) return null;
            if (name == "}") return null;
            if (!name.EndsWith(':')) return null; // stray token
            var node = new FbxNode { Name = name[..^1] };
            bool expectValue = true;
            while (true)
            {
                var t = tok.Peek();
                if (t == null) return node;
                if (t == "{")
                {
                    tok.Next();
                    while (tok.Peek() != null && tok.Peek() != "}") { var c = ReadAsciiNode(tok); if (c != null) node.Children.Add(c); }
                    tok.Next(); // }
                    return node;
                }
                if (t == ",") { tok.Next(); expectValue = true; continue; }
                if (t.EndsWith(':') && !t.StartsWith('"') || t == "}") return node; // next record
                if (!expectValue && tok.NewLineBefore) return node;
                tok.Next();
                node.Props.Add(AsciiValue(t));
                expectValue = false;
            }
        }

        static object AsciiValue(string t)
        {
            if (t.StartsWith('"')) return t.Substring(1, t.Length - 2).Replace("&quot;", "\"");
            if (t.StartsWith('*')) return t; // array size marker "*123"
            if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            return t; // e.g. T / Y flags
        }

        sealed class AsciiTokens
        {
            readonly string _s; int _p; string _peek; bool _peekNl;
            public bool NewLineBefore;
            public AsciiTokens(string s) { _s = s; }
            public string Peek() { if (_peek == null) _peek = Read(out _peekNl); return _peek; }
            public string Next() { var t = Peek(); NewLineBefore = _peekNl; _peek = null; return t; }

            string Read(out bool newline)
            {
                newline = false;
                while (_p < _s.Length)
                {
                    char c = _s[_p];
                    if (c == ';') { while (_p < _s.Length && _s[_p] != '\n') _p++; continue; }
                    if (c == '\n') newline = true;
                    if (char.IsWhiteSpace(c)) { _p++; continue; }
                    break;
                }
                if (_p >= _s.Length) return null;
                char ch = _s[_p];
                if (ch == '{' || ch == '}' || ch == ',') { _p++; return ch.ToString(); }
                if (ch == '"')
                {
                    int st = _p++;
                    while (_p < _s.Length && _s[_p] != '"') _p++;
                    _p++;
                    return _s.Substring(st, _p - st);
                }
                int start = _p;
                while (_p < _s.Length && !char.IsWhiteSpace(_s[_p]) && _s[_p] != ',' && _s[_p] != '{' && _s[_p] != '}')
                {
                    if (_s[_p] == ':') { _p++; break; }
                    _p++;
                }
                return _s.Substring(start, _p - start);
            }
        }
    }
}
