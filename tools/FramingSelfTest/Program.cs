using System.Buffers.Binary;
using System.Text;
using BotClient.ClientDriver.Sniff;
using BotClient.Protocol;

// 离线自测：不连客户端、不抓包，只喂合成样本，验证"自动定界"能不能把帧界认出来。
//   样本 A：经典 Mir2 魔数 DD CC BB AA + 长度 + 24 字节头（应当命中 magic/len@4/LE/header24）
//   样本 B：换服变体 魔数 11 22 33 44 + 长度 + 24 字节头（应当命中该变体，证明"换服无需改代码"）
//   样本 C：经典文本帧 '#'..'!'（应当判为 Classic）
//   样本 D：纯随机字节（高熵，应当判为不可定界/疑似加密）

int pass = 0, fail = 0;

void Check(string name, bool ok, string detail)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name} — {detail}");
    if (ok) pass++; else fail++;
}

byte[] BuildBinaryStream(byte[] magic, int frames, int seed)
{
    var rnd = new Random(seed);
    var ms = new MemoryStream();
    var idents = new ushort[] { Grobal2.SM_CERTIFICATION_SUCCESS, Grobal2.SM_QUERYCHR, Grobal2.SM_NEWCHR_SUCCESS, Grobal2.SM_CHGPASSWD_SUCCESS };
    for (int i = 0; i < frames; i++)
    {
        int len = rnd.Next(12, 220);
        var body = new byte[len];
        rnd.NextBytes(body);
        var head = new byte[24];
        Buffer.BlockCopy(magic, 0, head, 0, magic.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), (uint)len);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(16), idents[i % idents.Length]);
        ms.Write(head);
        ms.Write(body);
    }
    return ms.ToArray();
}

byte[] BuildTextStream(int frames)
{
    var ms = new MemoryStream();
    for (int i = 0; i < frames; i++)
    {
        ms.Write(Encoding.ASCII.GetBytes($"#{i % 10}{new string('x', 24)}!"));
    }
    return ms.ToArray();
}

Console.WriteLine("== 1) 经典 RunGate 魔数 ==");
var a = BuildBinaryStream(new byte[] { 0xDD, 0xCC, 0xBB, 0xAA }, 60, 11);
var ra = SplitterAutoDetector.Detect(a);
Console.WriteLine(ra.Report);
Check("认出经典 RunGate 定界",
    ra.Ok && ra.Best!.Magic.Equals("DDCCBBAA", StringComparison.OrdinalIgnoreCase)
    && ra.Best.LengthOffset == 4 && ra.Best.LengthSize == 4 && ra.Best.HeaderSize == 24,
    ra.Best?.Describe() ?? "无");

Console.WriteLine();
Console.WriteLine("== 2) 换服变体（魔数 11223344）==");
var b = BuildBinaryStream(new byte[] { 0x11, 0x22, 0x33, 0x44 }, 60, 22);
var rb = SplitterAutoDetector.Detect(b);
Console.WriteLine(rb.Report);
Check("认出该服自己的魔数定界",
    rb.Ok && rb.Best!.Magic.Equals("11223344", StringComparison.OrdinalIgnoreCase) && rb.Best.HeaderSize == 24,
    rb.Best?.Describe() ?? "无");

Console.WriteLine();
Console.WriteLine("== 3) 经典文本帧 ==");
var rc = SplitterAutoDetector.Detect(BuildTextStream(60));
Console.WriteLine(rc.Report);
Check("判为经典文本帧", rc.Ok && rc.Best!.Mode == FramingMode.ClassicMir, rc.Best?.Describe() ?? "无");

Console.WriteLine();
Console.WriteLine("== 4) 纯随机（高熵）==");
var rnd = new Random(99);
var d = new byte[65536];
rnd.NextBytes(d);
var rd = SplitterAutoDetector.Detect(d);
Console.WriteLine(rd.Report);
Check("不给出可信定界（熵 " + rd.Entropy.ToString("F2") + "）", !rd.Ok, rd.Best?.Describe() ?? "已如实否决");

Console.WriteLine();
Console.WriteLine("== 5) 帧边界切分正确性（含跨段喂入）==");
var stream = BuildBinaryStream(new byte[] { 0xDD, 0xCC, 0xBB, 0xAA }, 40, 33);
var p2 = new FramingProfile { Mode = FramingMode.MagicLength, Magic = "DDCCBBAA", LengthOffset = 4, LengthSize = 4, HeaderSize = 24 };
var slicer = new ProfileFrameSlicer(p2);
var got = new List<byte[]>();
var rnd2 = new Random(5);
int off = 0;
while (off < stream.Length)                       // 故意按 1..97 字节的随机段喂入，模拟 TCP 分段
{
    int take = Math.Min(rnd2.Next(1, 98), stream.Length - off);
    got.AddRange(slicer.Feed(stream.AsSpan(off, take)));
    off += take;
}
Check("跨 TCP 段重组后帧数一致", got.Count == 40 && slicer.DiscardedBytes == 0,
    $"切出 {got.Count}/40 帧，丢弃 {slicer.DiscardedBytes} 字节");

Console.WriteLine();
Console.WriteLine($"结果: {pass} 通过, {fail} 失败");
return fail == 0 ? 0 : 1;
