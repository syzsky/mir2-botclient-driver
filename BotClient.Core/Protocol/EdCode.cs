using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace BotClient.Protocol;

/// <summary>
/// Mir2 协议 6-bit 编解码, 逐字对应 gxx LoginGate\EDcode.pas 的
/// Encode6BitBuf_N / Decode6BitBuf_N (SurplusValue/SurplusCount 流式打包, +$3C 偏移, 无 XOR 种子, 无 S-box)。
/// 注意: gxx EDcode.pas 内另有 Encode6BitBuf_C/_S 修改版(带 S-box), 当前 LoginGate 走的是无后缀版本,
/// 而无后缀 Encode6BitBuf/Decode6BitBuf 又代理到 _N (EDcode.pas:177/190)。真实客户端与服务端一致。
/// </summary>
public static class EdCode
{
    private const byte Base = 0x3C;
    private const int BufferSize = 10_000;

    private static readonly Encoding Ascii = Encoding.ASCII;

    /// <summary>对应 Delphi GetEncodeSize(n) = (n * 4 + 2) div 3。</summary>
    public static int GetEncodeSize(int byteCount) => byteCount <= 0 ? 0 : (byteCount * 4 + 2) / 3;

    /// <summary>对应 Delphi GetDecodeSize(n) = Trunc(n * 3 / 4)。</summary>
    public static int GetDecodeSize(int encodedCount) => encodedCount <= 0 ? 0 : (encodedCount * 3) / 4;

    public static string EncodeMessage(CmdPack message)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref message, 1));
        return EncodeBytes(bytes);
    }

    public static CmdPack DecodeMessage(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return default;

        byte[] bytes = DecodeBytes(encoded);
        if (bytes.Length < CmdPack.Size)
            return default;

        return MemoryMarshal.Read<CmdPack>(bytes.AsSpan(0, CmdPack.Size));
    }

    public static string EncodeString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        byte[] bytes = GbkEncoding.Instance.GetBytes(value);
        return EncodeBytes(bytes);
    }

    public static string DecodeString(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return string.Empty;

        byte[] bytes = DecodeBytes(encoded);
        int len = IndexOfNull(bytes);
        if (len < 0)
            len = bytes.Length;

        return GbkEncoding.Instance.GetString(bytes, 0, len);
    }

    /// <summary>计算 byteCount 个字节经 EdCode 编码后的字符串长度(16字节头=22字符)。</summary>
    public static int GetEncodedLength(int byteCount)
    {
        if (byteCount <= 0)
            return 0;

        int cycles = byteCount / 3;
        int rem = byteCount % 3;
        return (cycles * 4) + (rem == 0 ? 0 : rem + 1);
    }

    public static string EncodeBuffer(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
            return string.Empty;
        if (buffer.Length >= BufferSize)
            return string.Empty;

        return EncodeBytes(buffer);
    }

    public static byte[] DecodeBytes(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return Array.Empty<byte>();

        ReadOnlySpan<char> src = encoded.AsSpan();
        int srcLen = src.Length;
        if (srcLen == 0)
            return Array.Empty<byte>();

        int destLen = GetDecodeSize(srcLen);
        byte[] dst = new byte[destLen];
        int dstPos = 0;

        byte surplusValue = 0;
        byte surplusCount = 0;

        for (int i = 0; i < srcLen; i++)
        {
            char ch = src[i];
            if (ch < Base || ch >= Base + 64)
                return dst; // Delphi: FillChar(Dest, DestLen, 0); Exit —— 非法字符, 返回已解码部分

            byte bSrc = unchecked((byte)(ch - Base));
            if (surplusCount > 0)
            {
                // CurrentBit8Value := SurplusValue or (bSrc shr (SurplusCount - 2))
                byte currentBit8 = unchecked((byte)(surplusValue | (bSrc >> (surplusCount - 2))));
                dst[dstPos++] = currentBit8;
                // SurplusValue := bSrc shl (8 - (SurplusCount - 2))  (按 Byte 截断, 与 Delphi 赋值给 Byte 变量一致)
                surplusValue = unchecked((byte)(bSrc << (8 - (surplusCount - 2))));
                surplusCount = unchecked((byte)(surplusCount - 2));
            }
            else
            {
                surplusCount = 6;
                surplusValue = unchecked((byte)((bSrc << 2) & 0xFC));
            }
        }

        return dst;
    }

    public static T DecodeBuffer<T>(string encoded) where T : unmanaged
    {
        if (string.IsNullOrEmpty(encoded))
            throw new FormatException($"Encoded buffer is empty for {typeof(T).Name}.");

        byte[] bytes = DecodeBytes(encoded);
        if (bytes.Length < Unsafe.SizeOf<T>())
            throw new FormatException($"Encoded buffer is too short for {typeof(T).Name}.");

        return MemoryMarshal.Read<T>(bytes);
    }

    /// <summary>安全解码值类型,失败返回false不抛异常。</summary>
    public static bool TryDecodeBuffer<T>(string encoded, out T result) where T : unmanaged
    {
        result = default;
        if (string.IsNullOrEmpty(encoded))
            return false;

        byte[] bytes;
        try
        {
            bytes = DecodeBytes(encoded);
        }
        catch
        {
            return false;
        }

        if (bytes.Length < Unsafe.SizeOf<T>())
            return false;

        result = MemoryMarshal.Read<T>(bytes);
        return true;
    }

    private static string EncodeBytes(ReadOnlySpan<byte> src)
    {
        if (src.IsEmpty)
            return string.Empty;

        if (src.Length >= BufferSize)
            return string.Empty;

        int outLen = GetEncodeSize(src.Length);
        Span<byte> dst = outLen <= 1024 ? stackalloc byte[outLen] : new byte[outLen];
        int dstPos = 0;

        int surplusValue = 0;   // 用 int 保持 32 位语义, 与 Delphi Byte shl 提升一致
        int surplusCount = 0;

        foreach (byte raw in src)
        {
            int c = raw;
            int shift = 2 + surplusCount;
            int cur6 = (surplusValue | (c >> shift)) & 0x3F;
            surplusValue = ((c << (8 - shift)) >> 2) & 0x3F;
            surplusCount += 2;

            dst[dstPos++] = unchecked((byte)(cur6 + Base));

            if (surplusCount == 6)
            {
                dst[dstPos++] = unchecked((byte)(surplusValue + Base));
                surplusValue = 0;
                surplusCount = 0;
            }
        }

        if (surplusCount > 0)
            dst[dstPos++] = unchecked((byte)(surplusValue + Base));

        return Ascii.GetString(dst[..dstPos]);
    }

    private static int IndexOfNull(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0)
                return i;
        }
        return -1;
    }
}
