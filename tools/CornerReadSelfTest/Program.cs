using BotClient.Vision;

// 左下角图例"视觉核对"离线自测：
//   ① 解析断言：真机常见的几种图例版式必须认得出来，乱码/空区域必须老实报"不可读"；
//   ② 比对断言：只在有依据时才报差异，绝不误报（宁可说"未比对"也不喊狼来了）；
//   ③ 源码护栏：确认这条通道走的是"只读像素拷贝"，没有出现会被客户端察觉的手段。
//
// 不需要游戏、不需要抓包、不需要 Windows（脚本本身跨平台），CI 每次 push 都会跑。

int passed = 0;
int failed = 0;

void Check(bool ok, string what)
{
    if (ok)
    {
        passed++;
        Console.WriteLine("  [ok]   " + what);
    }
    else
    {
        failed++;
        Console.WriteLine("  [FAIL] " + what);
    }
}

Console.WriteLine("== 1. 图例读数解析（OCR 文本 → 地图名 + 坐标）==");

CornerReading std = MapCornerParser.Parse("比奇省 (330,330)");
Check(std.Readable && std.MapName == "比奇省" && std.HasPos && std.X == 330 && std.Y == 330,
    "标准版式：名字 + 半角括号坐标");

CornerReading space = MapCornerParser.Parse("比奇省 (330, 330)");
Check(space.HasPos && space.X == 330 && space.Y == 330 && space.MapName == "比奇省",
    "坐标里有空格也能解出");

CornerReading full = MapCornerParser.Parse("比奇省（330，330）");
Check(full.HasPos && full.X == 330 && full.Y == 330 && full.MapName == "比奇省",
    "全角括号 + 全角逗号");

CornerReading deco = MapCornerParser.Parse("┃比奇省┃  坐标：12,34");
Check(deco.MapName == "比奇省" && deco.X == 12 && deco.Y == 34,
    "带装饰符与『坐标：』标签");

CornerReading bare = MapCornerParser.Parse("330,330");
Check(bare.Readable && bare.HasPos && bare.X == 330 && bare.Y == 330 && bare.MapName.Length == 0,
    "图例去掉了括号，裸数字也能认");

CornerReading onlyName = MapCornerParser.Parse("比奇省");
Check(onlyName.Readable && !onlyName.HasPos && onlyName.MapName == "比奇省",
    "只有地图名（尚未收到坐标包）");

CornerReading codeName = MapCornerParser.Parse("D1101 (12,34)");
Check(codeName.MapName == "D1101" && codeName.X == 12 && codeName.Y == 34,
    "画面显示编号而非中文名");

CornerReading twoLine = MapCornerParser.Parse("坐标\r\n(330,330)");
Check(twoLine.HasPos && twoLine.X == 330 && twoLine.Y == 330 && twoLine.MapName.Length == 0,
    "两行版式（换行折叠）+ 标签词不当成地图名");

CornerReading bracketNote = MapCornerParser.Parse("比奇省(推荐) (330,330)");
Check(bracketNote.MapName == "比奇省", "名字带括号备注时只取主名");

Check(!MapCornerParser.Parse("").Readable, "空文本 → 不可读");
Check(!MapCornerParser.Parse("   ").Readable, "全空白 → 不可读");
Check(!MapCornerParser.Parse(null).Readable, "null → 不可读");

Console.WriteLine();
Console.WriteLine("== 2. 与嗅探数据比对（只在有依据时报差异）==");

CornerCheck ok1 = MapCornerParser.Compare(std, "比奇省", "D1101", 330, 330);
Check(ok1.Ok && ok1.MapChecked, "地图名 + 坐标都对 → 一致（已核对地图名）");

CornerCheck ok2 = MapCornerParser.Compare(std, "比奇省(推荐)", "D1101", 330, 329);
Check(ok2.Ok, "名字带备注 / 坐标差 1 格（容差内）→ 一致");

CornerCheck ok3 = MapCornerParser.Compare(std, "比奇省", "D1101", 330, 334, posTolerance: 5);
Check(ok3.Ok, "自定义容差 5 格生效");

CornerCheck bad1 = MapCornerParser.Compare(std, "祖玛寺庙", "D1101", 330, 330);
Check(!bad1.Ok && bad1.Verdict == CornerVerdict.MapMismatch, "地图名对不上 → 报差异（此即嗅探错位的信号）");

CornerCheck bad2 = MapCornerParser.Compare(std, "比奇省", "D1101", 336, 330);
Check(!bad2.Ok && bad2.Verdict == CornerVerdict.PosMismatch, "坐标超出容差 → 报差异");

CornerCheck byCode = MapCornerParser.Compare(codeName, "", "D1101", 12, 34);
Check(byCode.Ok && byCode.MapChecked, "画面是编号、嗅探只有编号 → 按编号比对通过（算已核对）");

CornerCheck noTable = MapCornerParser.Compare(std, "", "D1101", 330, 330);
Check(noTable.Ok && !noTable.MapChecked,
    "嗅探侧没读到中文名表 → 只比坐标，地图名老实地记为『未比对』，不误报");

CornerCheck noPos = MapCornerParser.Compare(std, "比奇省", "D1101", 330, 330, checkPos: false);
Check(noPos.Ok && noPos.MapChecked, "关闭坐标比对时只看地图名");

CornerCheck unreadable = MapCornerParser.Compare(CornerReading.Unreadable, "比奇省", "D1101", 1, 1);
Check(!unreadable.Ok && unreadable.Verdict == CornerVerdict.NotReadable,
    "画面不可读 → NotReadable（日志要给出原因，而不是当成差异）");

Check(bad1.Describe().Contains("不一致") && bad1.Describe().Contains("祖玛寺庙"),
    "差异结论文案可直接进日志");
Check(ok1.Describe().Contains("一致") && noTable.Describe().Contains("未比对"),
    "一致/未比对文案区分清楚");

Console.WriteLine();
Console.WriteLine("== 3. 源码护栏（合规路径 + 接线）==");

string root = FindRepoRoot();
string reader = File.ReadAllText(Path.Combine(root, "BotClientDriverHostUi", "Vision", "ScreenCornerReader.cs"));
Check(reader.Contains("CopyFromScreen"), "读数走的是屏幕像素拷贝（等价于用户自己看屏幕）");
// 只查真正的代码行：注释里为了讲清边界会提到这些手段的名字，不算违规
string readerCode = StripComments(reader);
foreach (string forbidden in new[]
         {
             "PrintWindow", "SendMessage", "PostMessage", "SetWindowsHookEx",
             "ReadProcessMemory", "WriteProcessMemory", "CreateRemoteThread", "SetWinEventHook",
         })
{
    Check(!readerCode.Contains(forbidden), "代码里不得出现可能被客户端察觉的手段：" + forbidden);
}
Check(reader.Contains("InMemoryRandomAccessStream") && reader.Contains("OcrEngine"),
    "OCR 在本进程内用系统自带引擎完成（不联网、不上传画面）");

string runner = File.ReadAllText(Path.Combine(root, "BotClientDriverHostUi", "Host", "HostRunner.cs"));
Check(runner.Contains("CrossCheckCornerAsync") && runner.Contains("MapCornerParser.Compare"),
    "宿主确实接了这条校验通道");
Check(runner.Contains("CrossCheckCornerAsync(\"换图\""), "换图后自动核对一次");
Check(runner.Contains("MaybePeriodicCornerCheck"), "运行中有低频周期核对（间隔可配 0 关闭）");

string cfg = File.ReadAllText(Path.Combine(root, "BotClient.ClientDriver", "ClientDriverConfig.cs"));
Check(cfg.Contains("CornerOcrOptions"), "配置里有视觉核对开关（可一键关闭）");
Check(cfg.Contains("ReadOnlySniff"), "只读嗅探开关仍在（没有被这次改动动过）");

string parser = File.ReadAllText(Path.Combine(root, "BotClient.Core", "Vision", "MapCornerParser.cs"));
Check(!parser.Contains("System.Drawing") && !parser.Contains("DllImport"),
    "解析逻辑是纯逻辑：不截屏、不 P/Invoke，可离线自测");

Console.WriteLine();
Console.WriteLine($"结果：{passed} 通过, {failed} 失败");
return failed == 0 ? 0 : 1;

// 把行注释/文档注释剔掉，只留下真正的代码行（护栏只针对代码，注释里可以讲清边界）。
static string StripComments(string source)
{
    var sb = new System.Text.StringBuilder(source.Length);
    foreach (string line in source.Split('\n'))
    {
        string t = line.TrimStart();
        if (t.StartsWith("//", StringComparison.Ordinal)
            || t.StartsWith("*", StringComparison.Ordinal)
            || t.StartsWith("/*", StringComparison.Ordinal))
            continue;
        int idx = line.IndexOf("//", StringComparison.Ordinal);
        sb.AppendLine(idx >= 0 ? line[..idx] : line);
    }
    return sb.ToString();
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "BotClient.Core"))
            && Directory.Exists(Path.Combine(dir.FullName, "BotClientDriverHostUi")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("找不到仓库根目录（AppContext.BaseDirectory 向上没有 BotClient.Core）");
}
