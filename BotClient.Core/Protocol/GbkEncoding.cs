using System.Text;

namespace BotClient.Protocol;

/// <summary>
/// GBK 编码助手(脱机 Bot 仅用 ASCII 字段,这里保留 GBK 以兼容服务端中文账号/角色名)。
/// </summary>
public sealed class GbkEncoding
{
    public static GbkEncoding Instance { get; } = new();

    private readonly Encoding _gbk;

    private GbkEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            _gbk = Encoding.GetEncoding("GBK");
        }
        catch
        {
            _gbk = Encoding.GetEncoding(936);
        }
    }

    public byte[] GetBytes(string s) => _gbk.GetBytes(s);
    public string GetString(byte[] bytes, int index, int count) => _gbk.GetString(bytes, index, count);
    public string GetString(ReadOnlySpan<byte> span) => _gbk.GetString(span);
    public int GetByteCount(string s) => _gbk.GetByteCount(s);

    /// <summary>给读写 GBK 文本文件(天骥角色目录里的 ini/脚本)用的编码对象。</summary>
    public static Encoding Gbk => Instance._gbk;
}
