using BotClient.Protocol;

namespace BotClient.Net;

/// <summary>
/// 服务端发来的包(参考 MirServerPacket)。
/// Mir2 服务端包格式:可选 1 位序号 + 16 字节 EdCode 编码头部 + body。
/// </summary>
public readonly record struct MirServerPacket(string RawPayload, CmdPack Header, string BodyEncoded)
{
    /// <summary>
    /// RunGate 二进制帧的包体是 M2 原样送出的字节:系统消息/私聊/组队/行会/公告这类
    /// 是未编码的 GBK 文本 (对照 ClMain.ProcessMessageServerMsg 直接用 sData)。
    /// 名字/物品说明那类由 M2 调用方 EncodeString 过的包仍要用 EdCode.DecodeString。
    /// </summary>
    public string BodyGbk => BodyEncoded.Length == 0
        ? string.Empty
        : GbkEncoding.Instance.GetString(System.Text.Encoding.Latin1.GetBytes(BodyEncoded));

    /// <summary>包体原始字节(未解压)。gxx 的结构体包用这个,不要走 EdCode。</summary>
    public byte[] BodyBytes => GxxPayload.AsBytes(BodyEncoded);

    /// <summary>包体字节,M2 侧 zLibCompressBuffer 压缩过的已还原。</summary>
    public byte[] BodyInflated => GxxPayload.InflateIfNeeded(BodyBytes);
}

public static class MirPacketDecoder
{
    public static bool TryDecode(string rawPayload, out MirServerPacket packet)
    {
        packet = default;
        if (string.IsNullOrEmpty(rawPayload))
        {
            BotLog.Warn($"[decode] 空 payload");
            return false;
        }

        ReadOnlySpan<char> span = rawPayload.AsSpan();

        // 跳过可选的 1 位序号
        if (span.Length > 0 && span[0] is >= '0' and <= '9')
            span = span[1..];

        if (span.Length == 0)
        {
            BotLog.Warn($"[decode] payload 跳序号后为空, raw={rawPayload[..Math.Min(40, rawPayload.Length)]}");
            return false;
        }

        if (span[0] == '+')
        {
            // 内部 ActMessage
            CmdPack internalHeader = CmdPack.MakeDefaultMsg(Grobal2.ActMessage, 0, 0, 0, 0);
            packet = new MirServerPacket(rawPayload, internalHeader, span.ToString());
            BotLog.Trace($"[decode] ActMessage 包, bodyLen={span.Length}");
            return true;
        }

        if (span.Length < Grobal2.DEFBLOCKSIZE)
        {
            BotLog.Warn($"[decode] payload 长度 < 头部16, spanLen={span.Length} raw={rawPayload[..Math.Min(40, rawPayload.Length)]}");
            return false;
        }

        string headerEncoded = span[..Grobal2.DEFBLOCKSIZE].ToString();
        string bodyEncoded = span[Grobal2.DEFBLOCKSIZE..].ToString();

        CmdPack header = EdCode.DecodeMessage(headerEncoded);
        if (header.Ident == 0)
        {
            BotLog.Warn($"[decode] 头部 Ident=0(解码失败), headerEnc={headerEncoded} bodyEnc前20={bodyEncoded[..Math.Min(20, bodyEncoded.Length)]}");
            return false;
        }

        packet = new MirServerPacket(rawPayload, header, bodyEncoded);
        BotLog.Trace($"[decode] OK Ident={header.Ident} Recog={header.Recog} Param={header.Param} Tag={header.Tag} Series={header.Series} bodyLen={bodyEncoded.Length}");
        return true;
    }
}
