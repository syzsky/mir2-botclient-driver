using System.Text;
using BotClient.Protocol;

namespace BotClient.Protocol;

/// <summary>
/// LoginGate 上行包加密助手:
/// 1) 22 字符密钥包 → ProtocolPassword (4×DWORD 异或)。
/// 2) murmur 混合哈希, 逐字对应 LoginGate\ClientSession.pas ProcessCltData (VER_TYPE<>1 分支)。
/// 3) 包体加密链: 明文GBK → 6bit编码 → DES(IntToStr(pw)) → 6bit编码 → 线上;
///    与 gate 侧 DecodeString→DecryptDes→LoginSrv DecodeString 的解包链对应。
/// </summary>
public static class LoginGateCrypto
{
    /// <summary>解析 gate 连接后推送的 22 字符裸密钥包 → pw = t[0]^t[1]^t[2]^t[3]。</summary>
    public static uint DecodeKeyPacket(string encoded22)
    {
        byte[] bytes = EdCode.DecodeBytes(encoded22);
        if (bytes.Length < 16)
            throw new InvalidOperationException($"密钥包长度不足: 期望16字节, 实际{bytes.Length} (编码串={encoded22})");

        uint t0 = (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
        uint t1 = (uint)(bytes[4] | (bytes[5] << 8) | (bytes[6] << 16) | (bytes[7] << 24));
        uint t2 = (uint)(bytes[8] | (bytes[9] << 8) | (bytes[10] << 16) | (bytes[11] << 24));
        uint t3 = (uint)(bytes[12] | (bytes[13] << 8) | (bytes[14] << 16) | (bytes[15] << 24));
        return t3 ^ t0 ^ t1 ^ t2;
    }

    private static uint LE32(string s, int zeroBased) =>
        (uint)(s[zeroBased] | (s[zeroBased + 1] << 8) | (s[zeroBased + 2] << 16) | (s[zeroBased + 3] << 24));

    /// <summary>murmur 混合哈希, 输入为线上编码包体 ASCII 串。x86 移位计数 mod 32, 与 Delphi 编译产物一致。</summary>
    public static uint MixHash(string body, uint pw)
    {
        byte[] k = new byte[8];
        for (int i = 0; i < 8; i++)
        {
            int mask = 0xF << (i * 4);
            k[i] = (byte)(((long)pw & mask) & 0xFF);
        }

        uint a = 0x801B0D01, b = 0x5DAE409F, c = pw;
        int tempLen = body.Length;
        int pos = 0; // 0-based, 对应 Delphi k=1

        while (tempLen >= 12)
        {
            a = unchecked(a + LE32(body, pos));
            b = unchecked(b + LE32(body, pos + 4));
            c = unchecked(c + LE32(body, pos + 8));

            a = unchecked(a - b); a = unchecked(a - c); a ^= c >> (int)k[5];
            b = unchecked(b - c); b = unchecked(b - a); b ^= a << (int)k[7];
            c = unchecked(c - a); c = unchecked(c - b); c ^= b >> (int)k[2];
            a = unchecked(a - b); a = unchecked(a - c); a ^= c >> (int)k[4];
            b = unchecked(b - c); b = unchecked(b - a); b ^= a << (int)k[3];
            c = unchecked(c - a); c = unchecked(c - b); c ^= b >> (int)k[0];
            a = unchecked(a - b); a = unchecked(a - c); a ^= c >> (int)k[6];
            b = unchecked(b - c); b = unchecked(b - a); b ^= a << (int)k[1];
            c = unchecked(c - a); c = unchecked(c - b); c ^= b >> 3;

            pos += 12;
            tempLen -= 12;
        }

        c = unchecked(c + (uint)body.Length);

        // 尾部按字节累加 (Delphi 1-based sRecv[k+i])
        if (tempLen >= 11) c = unchecked(c + (unchecked((uint)body[pos + 10]) << 24));
        if (tempLen >= 10) c = unchecked(c + (unchecked((uint)body[pos + 9]) << 16));
        if (tempLen >= 9) c = unchecked(c + (unchecked((uint)body[pos + 8]) << 8));
        if (tempLen >= 8) b = unchecked(b + (unchecked((uint)body[pos + 7]) << 24));
        if (tempLen >= 7) b = unchecked(b + (unchecked((uint)body[pos + 6]) << 16));
        if (tempLen >= 6) b = unchecked(b + (unchecked((uint)body[pos + 5]) << 8));
        if (tempLen >= 5) b = unchecked(b + (uint)body[pos + 4]);
        if (tempLen >= 4) a = unchecked(a + (unchecked((uint)body[pos + 3]) << 24));
        if (tempLen >= 3) a = unchecked(a + (unchecked((uint)body[pos + 2]) << 16));
        if (tempLen >= 2) a = unchecked(a + (unchecked((uint)body[pos + 1]) << 8));
        if (tempLen >= 1) a = unchecked(a + (uint)body[pos]);

        a = unchecked(a - b); a = unchecked(a - c); a ^= c >> (int)k[5];
        b = unchecked(b - c); b = unchecked(b - a); b ^= a << (int)k[7];
        c = unchecked(c - a); c = unchecked(c - b); c ^= b >> (int)k[2];
        a = unchecked(a - b); a = unchecked(a - c); a ^= c >> (int)k[4];
        b = unchecked(b - c); b = unchecked(b - a); b ^= a << (int)k[3];
        // 末块 quirk: gate 源码此处是 b shl ArrKey[0] (ClientSession.pas:365), 与循环内 shr 不同
        c = unchecked(c - a); c = unchecked(c - b); c ^= b << (int)k[0];
        a = unchecked(a - b); a = unchecked(a - c); a ^= c >> (int)k[6];
        b = unchecked(b - c); b = unchecked(b - a); b ^= a << (int)k[1];
        c = unchecked(c - a); c = unchecked(c - b); c ^= b >> 3;

        return c;
    }

    /// <summary>加密明文包体 → 线上编码 body 字符串 (DES + 双层 6bit 编码)。</summary>
    public static string EncryptBody(string plainGbk, uint pw) =>
        EncryptBodyRaw(GbkEncoding.Instance.GetBytes(plainGbk), pw);

    /// <summary>加密原始字节载荷 (对应真实客户端 SendData := EncodeString/EncodeBuffer(payload) → EncryptDes → EncodeString 三步)。</summary>
    public static string EncryptBodyRaw(ReadOnlySpan<byte> payloadBytes, uint pw)
    {
        string inner = EdCode.EncodeBuffer(payloadBytes);
        byte[] innerBytes = Encoding.ASCII.GetBytes(inner);
        byte[] cipher = UnitDes.EncryptDes(innerBytes, Convert.ToString(pw));
        string outer = EdCode.EncodeBuffer(cipher);

        if (SelfTestEnabled)
        {
            // 客户端自校验: 完整还原 gate+LoginSrv 侧的解码链, 保证我们送出去的字节能解回原明文。
            // 反向链: outer → DecodeString → cipher → DecryptDes → inner(ASCII) → DecodeString → GBK 明文
            byte[] cipherBack = EdCode.DecodeBytes(outer);
            byte[] innerBack = UnitDes.DecryptDes(cipherBack, Convert.ToString(pw));
            string innerStrBack = Encoding.ASCII.GetString(innerBack);
            byte[] plainBack = EdCode.DecodeBytes(innerStrBack);
            string plainStrBack = GbkEncoding.Instance.GetString(plainBack);
            string plainStrOrig = GbkEncoding.Instance.GetString(payloadBytes);
            bool ok = plainStrBack == plainStrOrig;
            BotLog.Info($"[selftest] pw={pw} plainLen={payloadBytes.Length} innerLen={inner.Length} cipherLen={cipher.Length} outerLen={outer.Length} roundtrip={(ok ? "OK" : "FAIL")} got='{plainStrBack}'");
        }

        return outer;
    }

    /// <summary>调试开关: 打开时每次加密会做一次本地反解校验并写日志。</summary>
    public static bool SelfTestEnabled;

    /// <summary>构造带加密包体的上行 payload: 22 字符头(Recog 低位=hash) + 加密体。</summary>
    public static string BuildEncryptedPayload(ushort ident, string plainBody, uint pw,
        ushort param = 0, ushort tag = 0, ushort series = 0)
        => BuildEncryptedPayloadRaw(ident, GbkEncoding.Instance.GetBytes(plainBody), pw, param, tag, series);

    public static string BuildEncryptedPayloadRaw(ushort ident, ReadOnlySpan<byte> payloadBytes, uint pw,
        ushort param = 0, ushort tag = 0, ushort series = 0)
    {
        string body = EncryptBodyRaw(payloadBytes, pw);
        uint hash = MixHash(body, pw);
        var msg = CmdPack.MakeDefaultMsg(ident, hash, param, tag, series);
        return EdCode.EncodeMessage(msg) + body;
    }

    /// <summary>构造无包体的上行 payload (仅 22 字符头, 不加密不哈希)。</summary>
    public static string BuildPlainPayload(ushort ident, long recog = 0, ushort param = 0, ushort tag = 0, ushort series = 0)
    {
        var msg = CmdPack.MakeDefaultMsg(ident, recog, param, tag, series);
        return EdCode.EncodeMessage(msg);
    }
}
