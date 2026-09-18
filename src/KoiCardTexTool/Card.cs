using System;
using System.Collections.Generic;
using System.Text;

namespace KoiCardTexTool
{
    /// <summary>Locating the pieces of a KoiKatu card: thumbnail PNG, then the appended MessagePack docs.</summary>
    public static class Card
    {
        public static readonly byte[] PngMagic = { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        public static readonly byte[] JpgMagic = { 0xff, 0xd8, 0xff };

        /// <summary>Offset just past the IEND chunk, walking chunk lengths (a stray "IEND" inside
        /// compressed IDAT data must not fool us).</summary>
        public static int PngEnd(byte[] b)
        {
            if (b.Length < 8) return -1;
            for (int i = 0; i < 8; i++) if (b[i] != PngMagic[i]) return -1;
            int p = 8;
            while (p + 8 <= b.Length)
            {
                long len = ((long)b[p] << 24) | ((long)b[p + 1] << 16) | ((long)b[p + 2] << 8) | b[p + 3];
                bool iend = b[p + 4] == 'I' && b[p + 5] == 'E' && b[p + 6] == 'N' && b[p + 7] == 'D';
                long np = p + 12 + len;
                if (np > b.Length || len < 0) return -1;
                p = (int)np;
                if (iend) return p;
            }
            return -1;
        }

        /// <summary>Find a msgpack *key* by name (fixstr or str8/16/32), return offset just past it.
        /// str8/16/32 的「长度字节」紧贴名字前面、类型标记在它前面一个：
        /// 长键（>31 字符，例如 38 字符的 MaterialEditor 插件 GUID）必须走这一支，
        /// 早先这里错把 b[pre] 当类型标记，长键永远匹配不上。</summary>
        public static int FindKey(byte[] b, string name)
        {
            return FindKeyFrom(b, name, 0);
        }

        // ---- 键位置缓存（v2.24）：同一份 byte[] 上反复找同样的键，只扫一次 ----
        // 为什么需要：读取一张卡要依次找 TextureDictionary / MaterialTexturePropertyList /
        // MaterialShaderList 等好几个键。实测这些键都落在卡头几百 KB 内（PNG 缩略图结束于
        // 140~152 KB），所以单次扫描只有 ~2ms；缓存省下的是**重复扫描**与重复解析。
        // （真正的大头是整文件读盘与逐张贴图取字节，见 ScanParts 的 KOITEX_SCANTIME 输出。）
        // 用 ConditionalWeakTable 挂在 byte[] 上：字节数组被回收时缓存自动消失，不会泄漏。
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Dictionary<string, int>> keyCache
            = new System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Dictionary<string, int>>();

        /// <summary>找键名，结果缓存（同一个 byte[] 上同一个名字只扫一次）。找不到也缓存（-1）。</summary>
        public static int FindKeyCached(byte[] b, string name)
        {
            if (b == null || b.Length == 0) return -1;
            Dictionary<string, int> d;
            if (!keyCache.TryGetValue(b, out d)) { d = new Dictionary<string, int>(StringComparer.Ordinal); keyCache.Add(b, d); }
            int pos;
            if (d.TryGetValue(name, out pos)) return pos;
            long t0 = Environment.TickCount;
            pos = FindKeyFrom(b, name, 0);
            KeyScanCount++;
            KeyScanMs += Environment.TickCount - t0;
            d[name] = pos;
            return pos;
        }

        /// <summary>从 from 继续找下一个同名键（人物卡里同一个键出现多次时用）。
        /// from &lt;= 0 时走缓存版；否则是真扫描（"第 N 次出现"没法简单缓存）。</summary>
        public static int FindKeyFromCached(byte[] b, string name, int from)
        {
            if (b == null) return -1;
            if (from <= 0) return FindKeyCached(b, name);
            long t0 = Environment.TickCount;
            int pos = FindKeyFrom(b, name, from);
            KeyScanCount++;
            KeyScanMs += Environment.TickCount - t0;
            return pos;
        }

        /// <summary>诊断用：整文件键扫描的次数与累计毫秒（KOITEX_SCANTIME=1 时打印）。</summary>
        public static int KeyScanCount;
        public static long KeyScanMs;

        /// <summary>从 from 开始往后找键（人物卡里同一个键会出现多次，要逐个扫）。
        ///
        /// ⚠ 这是**整文件逐字节扫描**：人物卡 270 MB，一次调用就要扫最多 270 MB。
        /// 早先用的是手写 for + 首字节预筛，实测在 270 MB 卡上单次 ~180~400ms；
        /// 现在先用 .NET 8 向量化的 <c>MemoryExtensions.IndexOf</c> 定位候选，快一个数量级。
        /// 即便如此，**调用次数**才是关键 —— 见 TexTool.KeyIndex（一次扫描，位置缓存复用）。</summary>
        public static int FindKeyFrom(byte[] b, string name, int from)
        {
            var nb = Encoding.UTF8.GetBytes(name);
            if (nb.Length == 0) return -1;
            int i = Math.Max(0, from);
            var span = new ReadOnlySpan<byte>(b);
            while (i >= 0 && i + nb.Length < b.Length)
            {
                int hit = span.Slice(i).IndexOf(nb);
                if (hit < 0) return -1;
                i += hit;
                int pre = i - 1;
                if (pre < 0) { i++; continue; }
                byte c = b[pre];
                if (c >= 0xa0 && c <= 0xbf) { if ((c & 0x1f) == nb.Length) return i + nb.Length; }
                // str8/16/32：编码是 [marker][长度字节 n 个][名字]，marker 在 pre-n、长度在 pre-n+1..pre。
                // n 由 marker 决定，只能三个候选都试（长键如 38 字符的插件 GUID 走这里）。
                bool ok = false;
                for (int n = 1; n <= 4; n <<= 1)
                {
                    byte marker = n == 1 ? (byte)0xd9 : (n == 2 ? (byte)0xda : (byte)0xdb);
                    if (pre - n < 0 || b[pre - n] != marker) continue;
                    int ln = 0;
                    for (int k = 0; k < n; k++) ln = (ln << 8) | b[pre - n + 1 + k];
                    if (ln == nb.Length) { ok = true; break; }
                }
                if (ok) return i + nb.Length;
                i++;                                   // 这个候选不是键名，继续往后找
            }
            return -1;
        }

        /// <summary>TexID -> set of shader property names bound to it.</summary>
        public static Dictionary<object, List<string>> TextureProperties(byte[] b)
        {
            var res = new Dictionary<object, List<string>>();
            int pos = FindKeyCached(b, "MaterialTexturePropertyList");
            if (pos < 0) return res;
            object holder = MP.Read(b, ref pos);
            var br = holder as BinRef;
            if (br == null) return res;
            int p = br.Off;
            var arr = MP.Read(b, ref p) as MArr;
            if (arr == null) return res;
            foreach (var it in arr.Items)
            {
                var m = it as MMap;
                if (m == null) continue;
                object id = m.Get("TexID");
                string prop = m.Get("Property") as string;
                if (id == null || prop == null) continue;
                List<string> lst;
                if (!res.TryGetValue(id, out lst)) { lst = new List<string>(); res[id] = lst; }
                if (!lst.Contains(prop)) lst.Add(prop);
            }
            return res;
        }

        public static string PropsToString(List<string> l)
        {
            if (l == null || l.Count == 0) return "-";
            var c = new List<string>(l);
            c.Sort(StringComparer.Ordinal);
            return string.Join(",", c.ToArray());
        }

        // ---------------------------------------------------------------- 部位（槽位）
        // 贴图归属部位不用猜材质名：MaterialEditor 的每条贴图绑定自带
        //   {'ObjectType': 1, 'CoordinateIndex': 0, 'Slot': 8, 'MaterialName': 'Metal',
        //    'Property': 'BumpMap', 'TexID': 5, ...}
        // ObjectType 1 = 服装槽位（ChaFileDefine.ClothesKind），2 = 饰品槽位（各自编号）。
        // ClothesKind 的取值是直接从游戏 Koikatu_Data\Managed\Assembly-CSharp.dll 里读出来的。

        public const int ObjClothes = 1, ObjAccessory = 2;

        /// <summary>服装槽位名（游戏 ChaFileDefine.ClothesKind 原值 + 中文）。</summary>
        public static readonly string[] ClothesSlotNames = {
            "上衣 top", "下衣 bot", "胸罩 bra", "内裤 shorts", "手套 gloves",
            "连裤袜 panst", "袜子 socks", "鞋(内层) shoes_inner", "鞋(外层) shoes_outer"
        };

        /// <summary>槽位的机器可读名字（预设签名用它，改中文显示名不影响跨卡匹配）。</summary>
        public static readonly string[] ClothesSlotTokens = {
            "top", "bot", "bra", "shorts", "gloves", "panst", "socks", "shoes_inner", "shoes_outer"
        };

        /// <summary>槽位标识 → 机器可读文本：top/bot/.../acc3/bodyparts0。</summary>
        public static string SlotKey2Text(int key)
        {
            int ot = SlotObjType(key), sn = SlotIndex(key);
            if (ot == ObjAccessory) return "acc" + sn;
            if (ot >= 3) return "bodyparts" + sn;
            return sn >= 0 && sn < ClothesSlotTokens.Length ? ClothesSlotTokens[sn] : ("slot" + sn);
        }

        /// <summary>槽位标识：ObjectType 与 Slot 合成一个 int。
        /// 服装 0..8、饰品 1000+n、角色本体部件 2000+n（人物卡的 OT≥3 是 body/face/hair/eye），
        /// 三套编号天然不撞号。</summary>
        public static int SlotKey(int objType, int slot)
        {
            if (objType == ObjAccessory) return 1000 + slot;
            if (objType >= 3) return 2000 + slot;
            return slot;
        }

        public static int SlotObjType(int key)
        {
            if (key >= 2000) return 3;
            return key >= 1000 ? ObjAccessory : ObjClothes;
        }
        public static int SlotIndex(int key)
        {
            if (key >= 2000) return key - 2000;
            return key >= 1000 ? key - 1000 : key;
        }

        public static string SlotName(int key)
        {
            int ot = SlotObjType(key), sn = SlotIndex(key);
            if (ot >= 3) return "身体/脸/头发/眼睛";
            if (ot == ObjClothes)
                return sn >= 0 && sn < ClothesSlotNames.Length ? ClothesSlotNames[sn] : ("服装槽 " + sn);
            return "饰品槽 " + sn;
        }

        /// <summary>一条「材质+属性 → 贴图」绑定。</summary>
        public sealed class Bind
        {
            public int SlotKey;
            public string Material = "";
            public string Property = "";
            public object TexId;
        }

        /// <summary>材质用哪个 shader（MaterialShaderList 的一条）。
        /// 卡片里每条形如 {ObjectType, CoordinateIndex, Slot, MaterialName,
        ///                  ShaderName, ShaderNameOriginal, RenderQueue, RenderQueueOriginal}。
        /// ShaderName 是**当前实际用的**（可能是 mod 着色器，如 xukmi/MainAlphaPlus、KKUTS、Goo），
        /// ShaderNameOriginal 是作者当初用的那个（如 Shader Forge/main_opaque）。</summary>
        public sealed class MatShader
        {
            public int ObjType = ObjClothes, Slot = 0;
            public string Material = "", Shader = "", ShaderOriginal = "";
            public int SlotKey { get { return Card.SlotKey(ObjType, Slot); } }
        }

        /// <summary>读出 MaterialShaderList（没有这段数据时返回空表）。</summary>
        public static List<MatShader> MaterialShaders(byte[] b)
        {
            var res = new List<MatShader>();
            int pos = FindKeyCached(b, "MaterialShaderList");
            if (pos < 0) return res;
            var br = MP.Read(b, ref pos) as BinRef;
            if (br == null) return res;
            int p = br.Off;
            var arr = MP.Read(b, ref p) as MArr;
            if (arr == null) return res;
            foreach (var it in arr.Items)
            {
                var m = it as MMap;
                if (m == null) continue;
                res.Add(new MatShader
                {
                    ObjType = (int)(m.Get("ObjectType") as long? ?? ObjClothes),
                    Slot = (int)(m.Get("Slot") as long? ?? 0),
                    Material = m.Get("MaterialName") as string ?? "",
                    Shader = m.Get("ShaderName") as string ?? "",
                    ShaderOriginal = m.Get("ShaderNameOriginal") as string ?? ""
                });
            }
            return res;
        }

        /// <summary>读出全部贴图绑定（含槽位）。没有 MaterialEditor 数据时返回空表。</summary>
        public static List<Bind> SlotBindings(byte[] b)
        {
            var res = new List<Bind>();
            int pos = FindKeyCached(b, "MaterialTexturePropertyList");
            if (pos < 0) return res;
            var br = MP.Read(b, ref pos) as BinRef;
            if (br == null) return res;
            int p = br.Off;
            var arr = MP.Read(b, ref p) as MArr;
            if (arr == null) return res;
            foreach (var it in arr.Items)
            {
                var m = it as MMap;
                if (m == null) continue;
                object id = m.Get("TexID");
                if (id == null) continue;
                long ot = m.Get("ObjectType") as long? ?? ObjClothes;
                long sn = m.Get("Slot") as long? ?? 0;
                res.Add(new Bind
                {
                    SlotKey = SlotKey((int)ot, (int)sn),
                    Material = m.Get("MaterialName") as string ?? "",
                    Property = m.Get("Property") as string ?? "",
                    TexId = id
                });
            }
            return res;
        }

        public sealed class Info
        {
            public string Tag = "", HeaderVersion = "", Name = "", DataVersion = "";
            public int Parts;
            public bool IsCard;
            /// <summary>人物卡（Koikatu_F_*.png）：版本号后面没有卡名字符串，且数据是整套 ChaFile。</summary>
            public bool IsChara;
            public string PngMissing = "";
        }

        static string Len1(byte[] b, ref int p)
        {
            if (p >= b.Length) return "";
            int n = b[p++];
            if (p + n > b.Length) return "";
            var s = Encoding.UTF8.GetString(b, p, n);
            p += n;
            return s;
        }

        static int IndexOf(byte[] b, byte[] pat, int from)
        {
            for (int i = from; i + pat.Length <= b.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < pat.Length; j++) if (b[i + j] != pat[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        /// <summary>卡片头部信息：tag / 版本 / 卡名 / 部件数（拖进来时用于"直接读取"显示）。</summary>
        public static Info ReadInfo(byte[] b)
        {
            var i = new Info();
            int end = PngEnd(b);
            if (end < 0) { i.PngMissing = "不是 PNG / 没有 IEND"; return i; }
            if (b.Length <= end + 4) { i.PngMissing = "PNG 之后没有附加数据（不是衣服卡）"; return i; }
            int p = end + 4;                                   // int32 LE magic (100)
            i.Tag = Len1(b, ref p);
            i.HeaderVersion = Len1(b, ref p);
            i.IsCard = i.Tag.IndexOf("KoiKatu", StringComparison.OrdinalIgnoreCase) >= 0;
            // 衣服卡在版本号后面还有「卡名」这个字符串；人物卡没有（版本号后面直接是 ChaFile 数据），
            // 硬按衣服卡去读会把文件内容当成长度 → 读出一段乱码当卡名
            i.IsChara = i.Tag.IndexOf("Chara", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!i.IsChara) i.Name = Len1(b, ref p);
            var pat = new byte[] { 0xa7, (byte)'v', (byte)'e', (byte)'r', (byte)'s', (byte)'i', (byte)'o', (byte)'n' };
            int v = IndexOf(b, pat, p);
            if (v >= 0)
            {
                // the key is preceded by the map header (fixmap 0x8n / map16 0xde+2 / map32 0xdf+4):
                // try the three possible starts and take the first that parses as a map
                int[] starts = { v - 1, v - 3, v - 5 };
                foreach (int st in starts)
                {
                    if (st < 0) continue;
                    byte h = b[st];
                    bool plausible = (st == v - 1 && h >= 0x80 && h <= 0x8f)
                                  || (st == v - 3 && h == 0xde)
                                  || (st == v - 5 && h == 0xdf);
                    if (!plausible) continue;
                    try
                    {
                        int q = st;
                        var m = MP.Read(b, ref q) as MMap;
                        if (m != null && (m.Get("parts") != null || m.Get("version") != null))
                        {
                            i.DataVersion = m.Get("version") as string ?? "";
                            var parts = m.Get("parts") as MArr;
                            if (parts != null) i.Parts = parts.Items.Count;
                            break;
                        }
                    }
                    catch { }
                }
            }
            return i;
        }
    }
}
