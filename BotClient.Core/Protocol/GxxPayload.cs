using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BotClient.Protocol;

/// <summary>
/// gxx RunGate 下行包体的原始字节读取 + packed record 字段解析。
/// 下行帧体是 M2 原样送出的字节:文本包直接按 GBK 用;结构体包分两种——
/// SendSocketEx 直发原样结构体数组,或 M2 侧 zLibCompressBuffer 压缩过
/// (对照 Client-HGE\ClMain.pas:26404 ProcessMessageAbility 用 zLibDecompressString)。
/// </summary>
public static class GxxPayload
{
    public static byte[] AsBytes(string latin1Body) =>
        latin1Body.Length == 0 ? Array.Empty<byte>() : Encoding.Latin1.GetBytes(latin1Body);

    /// <summary>body 以 zlib 头开头且能完整解压则解压,否则原样返回(文本包/未压缩结构体包)。</summary>
    public static byte[] InflateIfNeeded(byte[] body)
    {
        if (body.Length < 3 || body[0] != 0x78) return body;
        if (((body[0] << 8) | body[1]) % 31 != 0) return body;   // zlib CMF/FLG 校验位
        try
        {
            // 跳过头部 2 字节,尾部 adler32 由流结束自然丢弃
            using var src = new MemoryStream(body, 2, body.Length - 2);
            using var deflate = new DeflateStream(src, CompressionMode.Decompress);
            using var dst = new MemoryStream(body.Length * 4);
            var buf = new byte[8192];
            int n;
            while ((n = deflate.Read(buf, 0, buf.Length)) > 0)
            {
                if (dst.Length + n > 16 * 1024 * 1024) return body;
                dst.Write(buf, 0, n);
            }
            return dst.Length == 0 ? body : dst.ToArray();
        }
        catch
        {
            return body;
        }
    }

    private static int S32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadInt32LittleEndian(b.Slice(off, 4));
    private static int U16(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off, 2));
    private static byte U8(ReadOnlySpan<byte> b, int off) => b[off];

    /// <summary>Delphi ShortString: 首字节为长度,GBK 编码。</summary>
    public static string ShortString(ReadOnlySpan<byte> b, int off, int maxLen)
    {
        int len = b[off];
        if (len > maxLen || off + 1 + len > b.Length) return string.Empty;
        return GbkEncoding.Instance.GetString(b.Slice(off + 1, len).ToArray());
    }

    // ============================================================
    //  TAbility (Common/Grobal2.pas:3859) — 全部 4 字节字段,共 216 字节
    //  前 24 个字段是需要的属性,其余是 CreditPoint + NewValue[30]。
    // ============================================================
    public const int AbilityMinSize = 96;

    public readonly record struct AbilityInfo(
        int Level, int Ac, int Mac, int Dc, int Mc, int Sc,
        int Hp, int Mp, int MaxHp, int MaxMp, int Exp, int MaxExp,
        int Weight, int MaxWeight, int WearWeight, int MaxWearWeight,
        int HandWeight, int MaxHandWeight);

    public static bool TryReadAbility(byte[] body, out AbilityInfo ab)
    {
        ab = default;
        ReadOnlySpan<byte> b = body;
        if (b.Length < AbilityMinSize) return false;
        // Level AC1 AC2 MAC1 MAC2 DC1 DC2 MC1 MC2 SC1 SC2 HP MP MaxHP MaxMP Exp MaxExp
        // Weight MaxWeight WearWeight MaxWearWeight HandWeight MaxHandWeight CreditPoint
        ab = new AbilityInfo(
            Level: S32(b, 0),
            Ac: S32(b, 8),          // AC2(服务端最终值)
            Mac: S32(b, 16),
            Dc: S32(b, 24),
            Mc: S32(b, 32),
            Sc: S32(b, 40),
            Hp: S32(b, 44),
            Mp: S32(b, 48),
            MaxHp: S32(b, 52),
            MaxMp: S32(b, 56),
            Exp: S32(b, 60),
            MaxExp: S32(b, 64),
            Weight: S32(b, 68),
            MaxWeight: S32(b, 72),
            WearWeight: S32(b, 76),
            MaxWearWeight: S32(b, 80),
            HandWeight: S32(b, 84),
            MaxHandWeight: S32(b, 88));
        return true;
    }

    // ============================================================
    //  TNewMessageBodyWL (Common/Grobal2.pas:3461) —— 41 字节,SM_STRUCK(31) 与
    //  SM_HEALTHSPELLCHANGED(53) 的包体都是它(ObjPlayer.pas:37509/37564/37628),
    //  包头放的不是血量:31 的 Param/Tag 是"伤害"的 LoWord/HiWord(:37516),
    //  53 的 Param/Tag 是"MP"的 LoWord/HiWord(:38227)。按包头读血量会把伤害当 HP。
    //  字段: lParam1=HP@0 lParam2=MaxHP@4 lTag1=伤害@8 lTag2=等级@12 lTag3=MaxMP@16
    //        lTag4=CharStatus@20 lTag5(Int64)=攻击者对象ID@24 BlastHitType(1B)@32
    //        ResID@33 ResStartIdx@37
    //  原版客户端同样只从这里取血量(ClMain.pas:26916-26926)。
    // ============================================================
    public const int NewMessageBodyWlSize = 41;

    public readonly record struct HealthWl(int Hp, int MaxHp, int Damage, int Level, int MaxMp, long AttackerId);

    public static bool TryReadHealthWl(byte[] body, out HealthWl wl)
    {
        wl = default;
        ReadOnlySpan<byte> b = body;
        if (b.Length < NewMessageBodyWlSize) return false;
        wl = new HealthWl(
            Hp: S32(b, 0),
            MaxHp: S32(b, 4),
            Damage: S32(b, 8),
            Level: S32(b, 12),
            MaxMp: S32(b, 16),
            AttackerId: BinaryPrimitives.ReadInt64LittleEndian(b.Slice(24, 8)));
        return true;
    }

    /// <summary>SM_HEALTHSPELLCHANGED 的轻量形态:ChangeHP(LongWord)+IsAttackFromHum(Boolean)=5 字节,
    /// 血量取 ChangeHP,MP 仍从包头算(ClMain.pas:26901)。</summary>
    public const int HealthSpellChangedInfoSize = 5;

    public static bool TryReadHealthChangedInfo(byte[] body, out int changeHp)
    {
        changeHp = 0;
        ReadOnlySpan<byte> b = body;
        if (b.Length < HealthSpellChangedInfoSize) return false;
        changeHp = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(0, 4));
        return true;
    }

    // ============================================================
    //  TClientItem (Grobal2.pas:3706) = TStdItem(329B) + MakeIndex..
    //  记录变长(本 build 1119B),整条记录长度由包长/nCount 反推。
    // ============================================================
    public const int StdItemSize = 329;

    /// <summary>TStdItem 里 Expand1 的字节偏移(逐件商品明细的成交价就藏在这)。
    /// 由 Grobal2.pas:3570 起的字段链累加:Name(61)+DBName(61)+StdMode(1)+Shape(2)+Weight(1)
    /// +AniCount(2)+Source(4)+Reserved(1)+NeedIdentify(1)+Looks(2)+DuraMax(2)+Reserved1(2)
    /// +HP..SC2(12×4)+Need(4)+NeedLevel(4)@192+Price(4)@196+OverLap(2)@200+Color(1)
    /// +Stock(4)+Light(4)+Horse(4) ⇒ Expand1@215。
    /// 锚点已被背包/装备包验证:StdMode=122、Looks=134、S.DuraMax=136、NeedLevel=192、OverLap=200。
    /// 服务端只在逐件明细里覆写它(ObjNpc.pas:3735),原版客户端同样读它(FState 明细表)。</summary>
    public const int Expand1Offset = 215;

    public readonly record struct ClientItemInfo(
        string Name, byte StdMode, int Shape, byte Weight, int Looks,
        int StdDuraMax, int MakeIndex, int Dura, int DuraMax, bool IsBind,
        int OverLap, int NeedLevel, int Expand1);

    /// <summary>读取一条 TClientItem。recordLen 由调用方按包长推得,用于越界保护。</summary>
    public static ClientItemInfo? ReadClientItem(ReadOnlySpan<byte> rec)
    {
        if (rec.Length < StdItemSize + 10) return null;
        return new ClientItemInfo(
            Name: ShortString(rec, 0, 60),
            StdMode: U8(rec, 122),
            Shape: U16(rec, 123),
            Weight: U8(rec, 125),
            Looks: U16(rec, 134),
            StdDuraMax: U16(rec, 136),
            MakeIndex: S32(rec, StdItemSize),
            Dura: U16(rec, StdItemSize + 4),
            DuraMax: U16(rec, StdItemSize + 6),
            IsBind: U8(rec, StdItemSize + 8) != 0,
            OverLap: U16(rec, 200),
            NeedLevel: S32(rec, 192),
            Expand1: S32(rec, Expand1Offset));
    }

    /// <summary>SM_SENDDETAILGOODSLIST(652) 包体里的一条"逐件商品"。</summary>
    public readonly record struct DetailGoods(string Name, int MakeIndex, int Dura, int DuraMax, int Price, byte StdMode);

    /// <summary>解析逐件明细包体:nCount 条等长 TClientItem 原样结构体数组(不是 '/' 文本)。
    /// stride 由"包体长度 / nCount"反推,与背包同一套。</summary>
    public static List<DetailGoods> ParseDetailGoods(ReadOnlySpan<byte> body, int stride)
    {
        var list = new List<DetailGoods>();
        if (stride <= 0) return list;
        for (int off = 0; off + stride <= body.Length; off += stride)
        {
            ClientItemInfo? ci = ReadClientItem(body.Slice(off, stride));
            if (ci == null) continue;
            list.Add(new DetailGoods(ci.Value.Name, ci.Value.MakeIndex, ci.Value.Dura,
                ci.Value.DuraMax, ci.Value.Expand1, ci.Value.StdMode));
        }
        return list;
    }

    // ============================================================
    //  TClientMagic (Grobal2.pas:3750) = Key/Level/NewLevel/CurTrain(7B)
    //  + TMagic_C(153B) + 3×DWORD。wMagicId 在 Def 内偏移 1,sMagicName 偏移 3。
    //  packed 记录无对齐,整条记录内偏移:MagicAttr 7、wMagicId 8、sMagicName 10..70、
    //  btEffectType 71、btEffect 72、wSpell 73、MaxTrain[16] 75..138、btTrainLv 139、
    //  dwMagicDelayTime 140。Delay 是服务端给客户端的唯一下发处(M2Share.pas:10269),
    //  BotCombatAI 要靠它算施法间隔,否则只能瞎猜限速值。
    //  注意:偏移按 Delphi7\Source\Common\Grobal2.pas:3733 推得(和上面 TClientItem 同一份声明),
    //  但真机抓包里至今没有过 211 —— 首次真机联调要看日志里 "[magic] 技能列表 ... Delay(ms):" 是不是合理值,
    //  乱掉就说明这台 M2 的记录布局与此源码不同,会自动退回 BotCombatAI.SpellIntervalMs 兜底。
    // ============================================================
    public const int ClientMagicMinSize = 7 + 64;
    public const int MagicDelayOffset = 140;

    /// <summary>DelayMs = Magic.DB 的 Delay,-1 表示这条记录太短或数值不可信。</summary>
    public readonly record struct MagicInfo(byte Key, byte Level, int MagicId, string Name, int Train, int DelayMs);

    public static MagicInfo? ReadClientMagic(ReadOnlySpan<byte> rec)
    {
        if (rec.Length < ClientMagicMinSize) return null;
        int delay = -1;
        if (rec.Length >= MagicDelayOffset + 4)
        {
            int raw = S32(rec, MagicDelayOffset);
            // 服务端字段是 LongWord,解出负数或几十秒以上说明偏移对不上(不是本版本记录布局),按未知处理
            if (raw >= 0 && raw <= 60_000) delay = raw;
        }
        return new MagicInfo(
            Key: U8(rec, 0),
            Level: U8(rec, 1),
            MagicId: U16(rec, 8),
            Name: ShortString(rec, 10, 60),
            Train: S32(rec, 3),
            DelayMs: delay);
    }

    // ============================================================
    //  SM_SENDGOODSLIST(645) 文本正文:服务端 ObjNpc.pas:2166 每条商品拼 6 段
    //    名字/子菜单/价格/库存或MakeIndex/每叠数量/Looks/
    //  原版客户端同样按 6 段切(ClMain.pas:28760 起 6 次 GetValidStr3_Ex)。
    //  第 4 段在 子菜单=0(StdMode<=4/42/31,即可叠加消耗品)时是库存 UserItem.MakeIndex,
    //  在 子菜单=1(逐件的装备)时只是库存件数。购买要把它当 itemserverindex 回传:
    //  原版 FState.pas:16807 就是 `SendBuyItem(g_nCurMerchant, pg.Stock, nC, pg.RealName, False)`,
    //  因为服务端 TMerchant.ClientBuyItem:3418 的条件是"名字相同 且 UserItem.MakeIndex = 传入值",
    //  传 0 一件都买不到。
    // ============================================================
    public readonly record struct MerchantGoods(string Name, int SubMenu, int Price, int StockOrMakeIndex, int Count, int Looks)
    {
        /// <summary>只有子菜单=0 时第 4 段才是 MakeIndex,可以直接下单买。</summary>
        public bool Buyable => SubMenu == 0 && StockOrMakeIndex > 0;
    }

    public static List<MerchantGoods> ParseMerchantGoods(string? body)
    {
        var list = new List<MerchantGoods>();
        if (string.IsNullOrEmpty(body)) return list;
        string[] seg = body.Split('/');                       // 空段也占位,不能 RemoveEmptyEntries
        if (seg[^1].Length == 0) seg = seg[..^1];             // 每条末尾都带 '/',去掉以免整表错位
        for (int i = 0; i + 5 < seg.Length; i += 6)
        {
            string name = seg[i].Trim();
            if (name.Length == 0) continue;
            list.Add(new MerchantGoods(
                Name: name,
                SubMenu: Num(seg[i + 1]),
                Price: Num(seg[i + 2]),
                StockOrMakeIndex: Num(seg[i + 3]),
                Count: Math.Max(1, Num(seg[i + 4])),
                Looks: Num(seg[i + 5])));
        }
        return list;
    }

    private static int Num(string s) => int.TryParse(s.Trim(), out int v) ? v : 0;
}
