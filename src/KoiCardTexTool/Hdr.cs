using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KoiCardTexTool
{
    /// <summary>
    /// Radiance RGBE (.hdr) 读写。衣服卡里会出现 HDR 贴图（例如 lilToon 的 ReflectionCubeTex，
    /// 4096x2160 展开的经纬环境图），它是 GDI+ 完全无法解码的格式，必须自己处理：
    /// 解出浮点像素 → 在线性空间做盒式缩放 → 再按 RLE 写回 RLE RGBE。
    ///
    /// 用 Radiance 官方 setcolr/colr_color 的配对公式，保证「解码再按原尺寸编码」逐字节还原：
    ///   解码 value = byte * 2^(E-136)        编码 scale = m*256/v, E = e+128
    /// （COLXS = 128；另一种 +0.5 的写法会有 1 LSB 误差，这里不用。）
    /// </summary>
    public static class Hdr
    {
        public sealed class Image
        {
            public int W, H;
            public float[] Rgb;              // 长度 = W*H*3，线性空间
            public List<string> HeaderLines = new List<string>();   // 原封不动保留（EXPOSURE 等）
            public string ResLine = "";
        }

        public static bool IsHdr(byte[] raw)
        {
            if (raw == null || raw.Length < 10) return false;
            if (raw[0] != 0x23 || raw[1] != 0x3F) return false;      // "#?"
            string s = Encoding.ASCII.GetString(raw, 0, Math.Min(64, raw.Length));
            return s.IndexOf("RADIANCE", StringComparison.Ordinal) >= 0
                || s.IndexOf("RGBE", StringComparison.Ordinal) >= 0;
        }

        /// <summary>从分辨率行取宽高（"-Y 2160 +X 4096"），失败返回 false。不做完整解码，扫描时用。</summary>
        public static bool PeekSize(byte[] raw, out int w, out int h)
        {
            w = h = 0;
            int pos = FindDataStart(raw, out _);
            if (pos < 0) return false;
            string line = ReadLine(raw, ref pos);
            return ParseRes(line, out h, out w);
        }

        // ---------------------------------------------------------------- decode

        public static Image Decode(byte[] raw)
        {
            int pos = FindDataStart(raw, out List<string> header);
            if (pos < 0) throw new InvalidDataException("不是有效的 HDR（找不到头部结束的空行）");

            string resLine = ReadLine(raw, ref pos);
            int w, h;
            if (!ParseRes(resLine, out h, out w)) throw new InvalidDataException("HDR 分辨率行无法解析: " + resLine);
            if (w <= 0 || h <= 0 || (long)w * h > 400L * 1000 * 1000) throw new InvalidDataException("HDR 尺寸异常: " + resLine);

            var img = new Image { W = w, H = h, Rgb = new float[(long)w * h * 3 <= int.MaxValue ? w * h * 3 : 0], HeaderLines = header, ResLine = resLine };
            if (img.Rgb.Length == 0) throw new InvalidDataException("HDR 太大");

            var scan = new byte[w * 4];       // 每个通道一行
            var ch = new byte[4][];
            for (int c = 0; c < 4; c++) ch[c] = new byte[w];

            for (int y = 0; y < h; y++)
            {
                bool rle = (w >= 8 && w <= 0x7fff) && pos + 4 <= raw.Length
                           && raw[pos] == 2 && raw[pos + 1] == 2
                           && (((raw[pos + 2] << 8) | raw[pos + 3]) == w);
                if (rle)
                {
                    pos += 4;
                    for (int c = 0; c < 4; c++) pos = DecodeChannel(raw, pos, ch[c], w);
                    for (int x = 0; x < w; x++) for (int c = 0; c < 4; c++) scan[x * 4 + c] = ch[c][x];
                }
                else
                {
                    if (pos + w * 4 > raw.Length) throw new InvalidDataException("HDR 数据截断 (y=" + y + ")");
                    Buffer.BlockCopy(raw, pos, scan, 0, w * 4);
                    pos += w * 4;
                }
                int o = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    int e = scan[x * 4 + 3];
                    if (e == 0) { img.Rgb[o] = img.Rgb[o + 1] = img.Rgb[o + 2] = 0f; }
                    else
                    {
                        float f = (float)Math.Pow(2.0, e - 136);
                        img.Rgb[o] = scan[x * 4] * f;
                        img.Rgb[o + 1] = scan[x * 4 + 1] * f;
                        img.Rgb[o + 2] = scan[x * 4 + 2] * f;
                    }
                    o += 3;
                }
            }
            return img;
        }

        static int DecodeChannel(byte[] raw, int pos, byte[] dst, int w)
        {
            int x = 0;
            while (x < w)
            {
                if (pos >= raw.Length) throw new InvalidDataException("HDR RLE 数据截断");
                int n = raw[pos++];
                if (n > 128)
                {
                    n -= 128;
                    if (pos >= raw.Length) throw new InvalidDataException("HDR RLE 数据截断(2)");
                    byte v = raw[pos++];
                    for (int i = 0; i < n && x < w; i++) dst[x++] = v;
                }
                else
                {
                    if (n == 0) throw new InvalidDataException("HDR RLE 游程长度为 0");
                    if (pos + n > raw.Length) throw new InvalidDataException("HDR RLE 数据截断(3)");
                    for (int i = 0; i < n && x < w; i++) dst[x++] = raw[pos++];
                }
            }
            return pos;
        }

        // ---------------------------------------------------------------- encode

        public static byte[] Encode(Image img)
        {
            return Encode(img.W, img.H, img.Rgb, img.HeaderLines);
        }

        public static byte[] Encode(int w, int h, float[] rgb, List<string> headerLines)
        {
            var ms = new MemoryStream();
            var sb = new List<string>(headerLines);
            bool hasFmt = false, hasSig = false;
            foreach (var l in sb)
            {
                if (l.StartsWith("FORMAT=", StringComparison.Ordinal)) hasFmt = true;
                if (l.StartsWith("#?", StringComparison.Ordinal)) hasSig = true;
            }
            if (!hasSig) sb.Insert(0, "#?RADIANCE");
            if (!hasFmt) sb.Add("FORMAT=32-bit_rle_rgbe");
            foreach (var l in sb) WriteAscii(ms, l + "\n");
            WriteAscii(ms, "\n");
            WriteAscii(ms, string.Format("-Y {0} +X {1}\n", h, w));

            var ch = new byte[4][];
            for (int c = 0; c < 4; c++) ch[c] = new byte[w];
            var enc = new byte[w * 4];
            bool rle = (w >= 8 && w <= 0x7fff);

            for (int y = 0; y < h; y++)
            {
                int o = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    float r = rgb[o], g = rgb[o + 1], b = rgb[o + 2];
                    o += 3;
                    float v = Math.Max(r, Math.Max(g, b));
                    if (!(v > 1e-32f)) { ch[0][x] = ch[1][x] = ch[2][x] = ch[3][x] = 0; continue; }
                    int e;
                    double m = Frexp(v, out e);
                    double scale = m * 256.0 / v;
                    ch[0][x] = Clamp(r * scale);
                    ch[1][x] = Clamp(g * scale);
                    ch[2][x] = Clamp(b * scale);
                    ch[3][x] = (byte)Math.Min(255, Math.Max(0, e + 128));
                }
                if (rle)
                {
                    ms.WriteByte(2); ms.WriteByte(2); ms.WriteByte((byte)(w >> 8)); ms.WriteByte((byte)(w & 0xff));
                    for (int c = 0; c < 4; c++) EncodeChannel(ms, ch[c], w);
                }
                else
                {
                    for (int x = 0; x < w; x++) { enc[x * 4] = ch[0][x]; enc[x * 4 + 1] = ch[1][x]; enc[x * 4 + 2] = ch[2][x]; enc[x * 4 + 3] = ch[3][x]; }
                    ms.Write(enc, 0, w * 4);
                }
            }
            return ms.ToArray();
        }

        static void EncodeChannel(Stream s, byte[] d, int w)
        {
            int x = 0;
            while (x < w)
            {
                // 先看能不能凑一段重复
                int run = 1;
                while (x + run < w && d[x + run] == d[x] && run < 127) run++;
                if (run >= 4)
                {
                    s.WriteByte((byte)(128 + run));
                    s.WriteByte(d[x]);
                    x += run;
                    continue;
                }
                // 否则输出字面量游程（最多 128 个），遇到 >=4 的重复就停
                int lit = 0;
                while (x + lit < w && lit < 128)
                {
                    if (x + lit + 3 < w && d[x + lit] == d[x + lit + 1] && d[x + lit] == d[x + lit + 2]
                        && d[x + lit] == d[x + lit + 3]) break;
                    lit++;
                }
                if (lit == 0) lit = 1;
                s.WriteByte((byte)lit);
                s.Write(d, x, lit);
                x += lit;
            }
        }

        // ---------------------------------------------------------------- resize

        /// <summary>线性空间盒式缩放（HDR 必须在线性空间取平均）。</summary>
        public static Image ResizeBox(Image src, int nw, int nh)
        {
            nw = Math.Max(1, nw); nh = Math.Max(1, nh);
            var dst = new Image { W = nw, H = nh, Rgb = new float[nw * nh * 3], HeaderLines = src.HeaderLines, ResLine = src.ResLine };
            double sx = (double)src.W / nw, sy = (double)src.H / nh;
            for (int y = 0; y < nh; y++)
            {
                int y0 = (int)(y * sy), y1 = Math.Min(src.H, Math.Max(y0 + 1, (int)((y + 1) * sy)));
                for (int x = 0; x < nw; x++)
                {
                    int x0 = (int)(x * sx), x1 = Math.Min(src.W, Math.Max(x0 + 1, (int)((x + 1) * sx)));
                    double ar = 0, ag = 0, ab = 0; int n = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        int o = (yy * src.W + x0) * 3;
                        for (int xx = x0; xx < x1; xx++)
                        { ar += src.Rgb[o]; ag += src.Rgb[o + 1]; ab += src.Rgb[o + 2]; o += 3; n++; }
                    }
                    if (n == 0) n = 1;
                    int d = (y * nw + x) * 3;
                    dst.Rgb[d] = (float)(ar / n); dst.Rgb[d + 1] = (float)(ag / n); dst.Rgb[d + 2] = (float)(ab / n);
                }
            }
            return dst;
        }

        /// <summary>只改分辨率行、像素不变的「重写」（用于验证编码器）。</summary>
        public static byte[] ReencodeSame(byte[] raw)
        {
            return Encode(Decode(raw));
        }

        // ---------------------------------------------------------------- helpers

        static int FindDataStart(byte[] raw, out List<string> header)
        {
            header = new List<string>();
            int pos = 0, lines = 0;
            while (pos < raw.Length)
            {
                string line = ReadLine(raw, ref pos);
                if (line.Length == 0) return pos;
                header.Add(line);
                if (++lines > 64) return -1;                 // 头部不可能这么长，多半不是 HDR
            }
            return -1;
        }

        static string ReadLine(byte[] raw, ref int pos)
        {
            var sb = new StringBuilder();
            while (pos < raw.Length && raw[pos] != 10)
            {
                if (sb.Length < 512) sb.Append((char)raw[pos]);
                pos++;
            }
            if (pos < raw.Length) pos++;                     // 跳过 \n
            return sb.ToString().TrimEnd('\r');
        }

        static void WriteAscii(Stream s, string t)
        {
            var b = Encoding.ASCII.GetBytes(t);
            s.Write(b, 0, b.Length);
        }

        static bool ParseRes(string line, out int h, out int w)
        {
            h = w = 0;
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) return false;
            if (!int.TryParse(parts[1], out h) || !int.TryParse(parts[3], out w)) return false;
            return true;                                     // 只支持 "-Y n +X m" 这种标准朝向
        }

        static double Frexp(double v, out int e)
        {
            e = 0;
            if (v == 0 || double.IsNaN(v) || double.IsInfinity(v)) return 0;
            e = (int)Math.Floor(Math.Log(v, 2.0)) + 1;
            double m = v / Math.Pow(2.0, e);
            while (m >= 1.0) { m /= 2.0; e++; }
            while (m < 0.5) { m *= 2.0; e--; }
            return m;
        }

        static byte Clamp(double v)
        {
            if (v <= 0) return 0;
            if (v >= 255) return 255;
            return (byte)v;
        }
    }
}
