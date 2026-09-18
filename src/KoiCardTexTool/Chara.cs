using System;
using System.Collections.Generic;
using System.Text;

namespace KoiCardTexTool
{
    /// <summary>Koikatu 人物卡（Koikatu_F_*.png）的解析。
    ///
    /// 与衣服卡的差别（实测两张参考卡）：
    ///   · 附加数据标记是「【KoiKatuChara】」，里面是整套 ChaFile（脸/身体/头发/参数 + 7 套换装）
    ///   · 贴图只有**一个** TextureDictionary（本卡的 229 MB / 269 MB 全在里面），
    ///     不像衣服卡那样一卡一字典
    ///   · 材质绑定的 ObjectType 多了一类：4 = 角色本体（cf_m_body 身体、cf_m_eyeline 眼线、
    ///     cf_m_hitomi 眼白、cf_m_sirome 白目…），1 = 服装槽、2 = 饰品
    ///   · 绑定条目自带 CoordinateIndex 0..6 = 第几套换装（School01/School02/Gym/Swim/Club/Plain/Pajamas）
    ///   · 同一张贴图会被**多套换装共用**（例如袜子贴图同时挂在换装1 和 换装7），
    ///     所以"只压第 N 套"必须沿用「所有拥有者都启用才动」的规则
    /// </summary>
    public static class Chara
    {
        /// <summary>与游戏 ChaFileDefine.CoordinateType 一致的 7 套换装。</summary>
        public static readonly string[] CoordNames = {
            "换装1 School01", "换装2 School02", "换装3 Gym", "换装4 Swim",
            "换装5 Club", "换装6 Plain", "换装7 Pajamas"
        };

        /// <summary>组号：0 = 角色本体（身体/脸/头发/眼睛），1..7 = 第 N 套换装。</summary>
        public const int BodyGroup = 0;

        public static string GroupName(int g)
        {
            if (g <= BodyGroup) return "角色本体";
            int i = g - 1;
            return i < CoordNames.Length ? CoordNames[i] : ("换装" + g);
        }

        /// <summary>是不是人物卡（看完 PNG 缩略图之后那个标记）。</summary>
        public static bool IsCharaCard(byte[] b)
        {
            var info = Card.ReadInfo(b);
            return info.IsCard && info.Tag.IndexOf("Chara", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsClothesCard(byte[] b)
        {
            var info = Card.ReadInfo(b);
            return info.IsCard && info.Tag.IndexOf("Clothes", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>一条「材质+属性 → 贴图」绑定（带组号）。</summary>
        public sealed class Bind
        {
            public int Group;                 // 0=本体，1..7=换装
            public int ObjType;               // 1=服装槽 2=饰品 >=4=角色本体
            public int Slot;
            public string Material = "";
            public string Property = "";
            public object TexId;
            /// <summary>打包成规则键：group * 100000 + (ObjectType, Slot) 的键。</summary>
            public int Key { get { return PackKey(Group, Card.SlotKey(ObjType, Slot)); } }
        }

        /// <summary>组的键：**(group+1) * 100000 + 部位键**。
        /// 注意 +1：组 0 是「角色本体」，若直接用 group*100000 会让本体键落在 slotKey 上，
        /// 与衣服卡的槽位键（&lt;1000）撞车 → 分组识别失效。衣服卡键 &lt; 100000，两边天然不冲突。</summary>
        public static int PackKey(int group, int slotKey) { return (group + 1) * 100000 + slotKey; }
        public static int KeyGroup(int key) { return key / 100000 - 1; }
        public static int KeySlot(int key) { return key % 100000; }
        public static bool IsCharaKey(int key) { return key >= 100000; }

        /// <summary>组 → 名字：本体取 group 0（OT≥3 的材质没有换装归属）。</summary>
        public static int GroupOf(int objType, object coord)
        {
            if (objType >= 3) return BodyGroup;                     // 身体/脸/头发/眼睛
            int ci = (coord is long) ? (int)(long)coord : (coord is int ? (int)coord : 0);
            if (ci < 0) return BodyGroup;
            return ci + 1;
        }

        /// <summary>读出全部绑定。人物卡里 MaterialTexturePropertyList 可能出现多次（本体 + 各换装），
        /// 所以要把所有出现位置都扫一遍，只取"值是 bin 且能解析成数组"的那些。</summary>
        public static List<Bind> Bindings(byte[] b)
        {
            var res = new List<Bind>();
            int from = 0;
            while (true)
            {
                int kpos = Card.FindKeyFromCached(b, "MaterialTexturePropertyList", from);
                if (kpos < 0) break;
                from = kpos;
                int p = kpos;
                var br = MP.Read(b, ref p) as BinRef;
                if (br == null) continue;
                int q = br.Off;
                var arr = MP.Read(b, ref q) as MArr;
                if (arr == null) continue;
                foreach (var it in arr.Items)
                {
                    var m = it as MMap;
                    if (m == null) continue;
                    object id = m.Get("TexID");
                    if (id == null) continue;
                    long ot = m.Get("ObjectType") as long? ?? 1;
                    long sn = m.Get("Slot") as long? ?? 0;
                    var bd = new Bind
                    {
                        ObjType = (int)ot,
                        Slot = (int)sn,
                        Material = m.Get("MaterialName") as string ?? "",
                        Property = m.Get("Property") as string ?? "",
                        TexId = id
                    };
                    bd.Group = GroupOf(bd.ObjType, m.Get("CoordinateIndex"));
                    res.Add(bd);
                }
            }
            return res;
        }

        /// <summary>组 × 部位 的显示名，例如「换装4 Swim · 上衣 top」。KName() 会用它。</summary>
        public static string KeyName(int key)
        {
            return GroupName(KeyGroup(key)) + " · " + Card.SlotName(KeySlot(key));
        }

        /// <summary>「角色本体」绑定签名（不带组名前缀）。
        /// 用途：同款检测。人物卡的换装/材质因卡而异（不同角色穿的衣服不一样），
        /// 但**每张人物卡都有身体/脸/头发/眼睛**这套绑定 → 用它来判断"这张是不是人物卡"最稳。
        /// 注意：不能用「组·部位」全量签名去做这个判断，那样两张不同角色卡的匹配度会接近 0，
        /// 结果是把本来该压的卡全判成"不是同款"跳过（这就是 2.0 版人物卡压不动的真因）。</summary>
        public static string BodySig(Bind bd)
        {
            return Card.SlotKey2Text(Card.SlotKey(bd.ObjType, bd.Slot)) + "|" +
                   TexTool.NormMaterial(bd.Material) + "|" + (bd.Property ?? "");
        }

        /// <summary>整卡的「本体」签名集合。</summary>
        public static HashSet<string> BodySigs(byte[] b)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bd in Bindings(b))
                if (bd.ObjType >= 3) set.Add(BodySig(bd));
            return set;
        }
    }
}
