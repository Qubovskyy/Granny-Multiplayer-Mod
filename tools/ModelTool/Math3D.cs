namespace GrannyModelTool
{
    struct Vec3
    {
        public double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public Vec3 Normalized { get { double l = Length; return l > 1e-12 ? this * (1 / l) : new Vec3(0, 1, 0); } }
        public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
    }

    struct Quat
    {
        public double X, Y, Z, W;
        public Quat(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }
        public static readonly Quat Identity = new(0, 0, 0, 1);
        public Quat Normalized { get { double l = Math.Sqrt(X * X + Y * Y + Z * Z + W * W); return l > 1e-12 ? new Quat(X / l, Y / l, Z / l, W / l) : Identity; } }
        public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3}, {W:F3})";
    }

    /// <summary>4×4 matrix, column vectors (p' = M·p), stored row-major: M[r, c].</summary>
    sealed class Mat4
    {
        public readonly double[] M = new double[16];
        public double this[int r, int c] { get => M[r * 4 + c]; set => M[r * 4 + c] = value; }

        public static Mat4 Identity() { var m = new Mat4(); m[0, 0] = m[1, 1] = m[2, 2] = m[3, 3] = 1; return m; }
        public static Mat4 Translation(Vec3 t) { var m = Identity(); m[0, 3] = t.X; m[1, 3] = t.Y; m[2, 3] = t.Z; return m; }
        public static Mat4 Scale(Vec3 s) { var m = Identity(); m[0, 0] = s.X; m[1, 1] = s.Y; m[2, 2] = s.Z; return m; }
        public static Mat4 Diag(double x, double y, double z) => Scale(new Vec3(x, y, z));

        /// <summary>An FBX matrix (16 doubles, column by column) as a <see cref="Mat4"/>.</summary>
        public static Mat4 FromFbx(double[] a)
        {
            var m = new Mat4();
            for (int c = 0; c < 4; c++) for (int r = 0; r < 4; r++) m[r, c] = a[c * 4 + r];
            return m;
        }

        public static Mat4 RotX(double deg) { double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a); var m = Identity(); m[1, 1] = c; m[1, 2] = -s; m[2, 1] = s; m[2, 2] = c; return m; }
        public static Mat4 RotY(double deg) { double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a); var m = Identity(); m[0, 0] = c; m[0, 2] = s; m[2, 0] = -s; m[2, 2] = c; return m; }
        public static Mat4 RotZ(double deg) { double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a); var m = Identity(); m[0, 0] = c; m[0, 1] = -s; m[1, 0] = s; m[1, 1] = c; return m; }

        /// <summary>
        /// FBX Euler rotation (degrees) in the node's rotation order: 0 XYZ, 1 XZY, 2 YZX, 3 YXZ, 4 ZXY, 5 ZYX
        /// ("XYZ" = first about X, then Y, then Z, so the matrix is Rz·Ry·Rx).
        /// </summary>
        public static Mat4 Euler(Vec3 e, int order)
        {
            var x = RotX(e.X); var y = RotY(e.Y); var z = RotZ(e.Z);
            return order switch
            {
                1 => y * z * x,   // XZY
                2 => x * z * y,   // YZX
                3 => z * x * y,   // YXZ
                4 => y * x * z,   // ZXY
                5 => x * y * z,   // ZYX
                _ => z * y * x,   // XYZ (and spheric)
            };
        }

        public static Mat4 operator *(Mat4 a, Mat4 b)
        {
            var r = new Mat4();
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    double s = 0;
                    for (int k = 0; k < 4; k++) s += a[i, k] * b[k, j];
                    r[i, j] = s;
                }
            return r;
        }

        public Vec3 Point(Vec3 p) => new(this[0, 0] * p.X + this[0, 1] * p.Y + this[0, 2] * p.Z + this[0, 3],
                                         this[1, 0] * p.X + this[1, 1] * p.Y + this[1, 2] * p.Z + this[1, 3],
                                         this[2, 0] * p.X + this[2, 1] * p.Y + this[2, 2] * p.Z + this[2, 3]);
        public Vec3 Vector(Vec3 p) => new(this[0, 0] * p.X + this[0, 1] * p.Y + this[0, 2] * p.Z,
                                          this[1, 0] * p.X + this[1, 1] * p.Y + this[1, 2] * p.Z,
                                          this[2, 0] * p.X + this[2, 1] * p.Y + this[2, 2] * p.Z);

        public Mat4 Inverse()
        {
            var a = (double[])M.Clone();
            var inv = Identity().M;
            for (int col = 0; col < 4; col++)
            {
                int piv = col; double best = Math.Abs(a[col * 4 + col]);
                for (int r = col + 1; r < 4; r++) { double v = Math.Abs(a[r * 4 + col]); if (v > best) { best = v; piv = r; } }
                if (best < 1e-15) return Identity();
                if (piv != col)
                    for (int k = 0; k < 4; k++)
                    {
                        (a[col * 4 + k], a[piv * 4 + k]) = (a[piv * 4 + k], a[col * 4 + k]);
                        (inv[col * 4 + k], inv[piv * 4 + k]) = (inv[piv * 4 + k], inv[col * 4 + k]);
                    }
                double d = a[col * 4 + col];
                for (int k = 0; k < 4; k++) { a[col * 4 + k] /= d; inv[col * 4 + k] /= d; }
                for (int r = 0; r < 4; r++)
                {
                    if (r == col) continue;
                    double f = a[r * 4 + col];
                    if (f == 0) continue;
                    for (int k = 0; k < 4; k++) { a[r * 4 + k] -= f * a[col * 4 + k]; inv[r * 4 + k] -= f * inv[col * 4 + k]; }
                }
            }
            var res = new Mat4(); Array.Copy(inv, res.M, 16); return res;
        }

        public Vec3 TranslationPart => new(this[0, 3], this[1, 3], this[2, 3]);

        /// <summary>Translation, rotation and (signed) scale of an affine matrix without shear.</summary>
        public void Decompose(out Vec3 t, out Quat q, out Vec3 s)
        {
            t = TranslationPart;
            var c0 = new Vec3(this[0, 0], this[1, 0], this[2, 0]);
            var c1 = new Vec3(this[0, 1], this[1, 1], this[2, 1]);
            var c2 = new Vec3(this[0, 2], this[1, 2], this[2, 2]);
            double sx = c0.Length, sy = c1.Length, sz = c2.Length;
            if (Vec3.Dot(Vec3.Cross(c0, c1), c2) < 0) sx = -sx; // mirrored: one negative scale
            s = new Vec3(sx, sy, sz);
            c0 = sx != 0 ? c0 * (1 / sx) : new Vec3(1, 0, 0);
            c1 = sy != 0 ? c1 * (1 / sy) : new Vec3(0, 1, 0);
            c2 = sz != 0 ? c2 * (1 / sz) : new Vec3(0, 0, 1);
            q = FromBasis(c0, c1, c2);
        }

        /// <summary>Quaternion of a rotation matrix given by its columns.</summary>
        public static Quat FromBasis(Vec3 c0, Vec3 c1, Vec3 c2)
        {
            double m00 = c0.X, m10 = c0.Y, m20 = c0.Z, m01 = c1.X, m11 = c1.Y, m21 = c1.Z, m02 = c2.X, m12 = c2.Y, m22 = c2.Z;
            double tr = m00 + m11 + m22;
            Quat q;
            if (tr > 0) { double s = Math.Sqrt(tr + 1) * 2; q = new Quat((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25 * s); }
            else if (m00 > m11 && m00 > m22) { double s = Math.Sqrt(1 + m00 - m11 - m22) * 2; q = new Quat(0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s); }
            else if (m11 > m22) { double s = Math.Sqrt(1 + m11 - m00 - m22) * 2; q = new Quat((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s); }
            else { double s = Math.Sqrt(1 + m22 - m00 - m11) * 2; q = new Quat((m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s); }
            return q.Normalized;
        }
    }
}
