using System.Drawing;
using System.Drawing.Imaging;

namespace GrannyModelTool
{
    /// <summary>Minimal TGA reader (true colour 24 / 32 bit and greyscale, plain or RLE) – Windows cannot open TGA itself.</summary>
    static class Tga
    {
        public static Bitmap Decode(byte[] d)
        {
            if (d == null || d.Length < 18) return null;
            int idLen = d[0], cmapType = d[1], type = d[2];
            int w = d[12] | d[13] << 8, h = d[14] | d[15] << 8, bpp = d[16], desc = d[17];
            if (cmapType != 0 || w <= 0 || h <= 0 || !(type == 2 || type == 3 || type == 10 || type == 11)) return null;
            int bytes = bpp / 8;
            if (bytes < 1 || bytes > 4) return null;
            int p = 18 + idLen;
            var px = new byte[w * h * 4];
            bool rle = type >= 9;
            int count = w * h, i = 0;
            byte[] ReadPixel()
            {
                var c = new byte[4];
                if (bytes == 1) { c[0] = c[1] = c[2] = d[p]; c[3] = 255; }
                else { c[0] = d[p]; c[1] = d[p + 1]; c[2] = bytes >= 3 ? d[p + 2] : (byte)0; c[3] = bytes == 4 ? d[p + 3] : (byte)255; }
                p += bytes;
                return c; // B G R A
            }
            while (i < count && p < d.Length)
            {
                if (rle)
                {
                    int head = d[p++];
                    int n = (head & 0x7F) + 1;
                    if ((head & 0x80) != 0) { var c = ReadPixel(); for (int k = 0; k < n && i < count; k++, i++) Array.Copy(c, 0, px, i * 4, 4); }
                    else for (int k = 0; k < n && i < count; k++, i++) Array.Copy(ReadPixel(), 0, px, i * 4, 4);
                }
                else { Array.Copy(ReadPixel(), 0, px, i * 4, 4); i++; }
            }
            bool topDown = (desc & 0x20) != 0;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
            {
                int srcRow = topDown ? y : h - 1 - y;
                System.Runtime.InteropServices.Marshal.Copy(px, srcRow * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
            }
            bmp.UnlockBits(bd);
            return bmp;
        }
    }
}
