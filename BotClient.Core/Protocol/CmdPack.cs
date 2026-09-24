using System.Runtime.InteropServices;

namespace BotClient.Protocol;

/// <summary>
/// gxx TDefaultMessage: Recog 为 Int64, 头共 16 字节 (编码后 22 字符)。
/// 对应 Common\Grobal2.pas TDefaultMessage。
/// </summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = Size)]
public struct CmdPack
{
    public const int Size = 16;

    [FieldOffset(0)] public long Recog;
    [FieldOffset(8)] public ushort Ident;
    [FieldOffset(10)] public ushort Param;
    [FieldOffset(12)] public ushort Tag;
    [FieldOffset(14)] public ushort Series;

    // Recog 低 32 位视图 (旧客户端对象 UID)
    [FieldOffset(0)] public int UID;

    // 与主字段对齐的重叠视图
    [FieldOffset(8)] public ushort Cmd;
    [FieldOffset(10)] public ushort X;
    [FieldOffset(12)] public ushort Y;
    [FieldOffset(14)] public ushort Direct;

    [FieldOffset(0)] public int ID1;
    [FieldOffset(8)] public ushort Cmd1;
    [FieldOffset(10)] public int ID2;

    [FieldOffset(0)] public ushort PosX;
    [FieldOffset(2)] public ushort PosY;
    [FieldOffset(8)] public ushort Cmd2;
    [FieldOffset(10)] public ushort IDLo;
    [FieldOffset(12)] public ushort Magic;
    [FieldOffset(14)] public ushort IDHi;

    /// <summary>Recog 低 32 位视图,仅用于服务端放数值(金币/经验/负重/价格/MakeIndex)的消息。
    /// 对象身份必须用完整 Recog (Int64):64 位 M2 填的是 NativeInt(Self) 指针。</summary>
    public readonly int RecogI => unchecked((int)Recog);

    public static CmdPack MakeDefaultMsg(ushort ident, long recog, ushort param, ushort tag, ushort series) =>
        new()
        {
            Recog = recog,
            Ident = ident,
            Param = param,
            Tag = tag,
            Series = series
        };

    /// <summary>Delphi MakeWord(Lo, Hi)。</summary>
    public static ushort MakeWord(byte lo, byte hi) => (ushort)(lo | (hi << 8));

    /// <summary>Delphi MakeLong(LoWord, HiWord),结果放 Recog (Int64) 时使用。</summary>
    public static long MakeLong(int loWord, int hiWord) => (uint)loWord | ((uint)hiWord << 16);

    /// <summary>公告确认包。原版 ClMain.pas:29507;首包为空公告时 noticeCode 传 0。</summary>
    public static CmdPack MakeLoginNoticeOk(uint noticeCode) =>
        MakeDefaultMsg(Grobal2.CM_LOGINNOTICEOK,
            MakeLong(Grobal2.SCREEN_WIDTH, Grobal2.SCREEN_HEIGHT),
            (ushort)(noticeCode & 0xFFFF),
            (ushort)(noticeCode >> 16),
            MakeWord(Grobal2.CLIENT_UI_TYPE, Grobal2.VIEW_GRID_COUNT));
}
