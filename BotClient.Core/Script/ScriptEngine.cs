namespace BotClient.Script;

/// <summary>脚本命令的落地端。引擎只负责"这一句该做哪个动作",怎么发包、怎么等回包全在实现里:
/// 真机是包着 BotRuntime 的 BotScriptApi,离线自检是一个只记流水账的假实现。
/// 方法一律不回传成败:天骥的脚本本身不读命令结果,失败靠下一轮 如果 重新判断现状。</summary>
public interface IScriptApi
{
    /// <summary>取值函数走这条路(和引擎共用同一个对象,省得两边对不上)。</summary>
    IValueSource Src { get; }

    void Display(string text);
    Task SayAsync(string text, CancellationToken ct);
    Task WaitAsync(int ms, CancellationToken ct);

    /// <summary>走到 / 走到附近 / 边打边走到 / 边打边走到附近。near=true 时到附近即可,fight=true 时一路打怪捡物。</summary>
    Task WalkToAsync(string map, int x, int y, bool fight, bool near, CancellationToken ct);
    Task WalkToNpcAsync(string npc, string map, int x, int y, CancellationToken ct);
    Task DoorToDoorAsync(string mapA, int x1, int y1, string mapB, int x2, int y2, bool fight, CancellationToken ct);
    Task RandomMoveAsync(string map, int x, int y, CancellationToken ct);
    Task RandomMoveStopAsync(CancellationToken ct);
    Task TeleportAsync(int x, int y, CancellationToken ct);

    Task FindNpcAsync(string name, string map, int x, int y, CancellationToken ct);
    Task TalkNpcAsync(string name, CancellationToken ct);
    Task TalkNpcAtAsync(int x, int y, CancellationToken ct);
    Task AttackNpcAsync(string name, int times, CancellationToken ct);
    /// <summary>攻击坐标[X,Y]:打这一格上的玩家或怪物。</summary>
    Task AttackAtAsync(int x, int y, CancellationToken ct);
    /// <summary>选择[内容] / 选择_加强[内容]:点当前这一页 NPC 对话里的某一项,不能重新点 NPC(会翻页)。</summary>
    Task ChooseAsync(string text, bool strong, CancellationToken ct);
    /// <summary>选择位置[N]:按 1 起的序号点当前这一页对话。</summary>
    Task ChooseAtAsync(int index, CancellationToken ct);

    Task UseItemAsync(string name, CancellationToken ct);
    Task EquipAsync(string item, string slot, CancellationToken ct);
    Task TakeOffAsync(string slot, CancellationToken ct);
    Task DropAsync(string item, int count, CancellationToken ct);
    Task DropGoldAsync(int amount, CancellationToken ct);
    /// <summary>购买/强行购买,count=0 表示"有多少买多少";slot 非空时买完直接穿上,repair 是"特修/修理/强行修理"这一串。</summary>
    Task BuyAsync(string item, int count, string? slot, string? repair, bool force, CancellationToken ct);
    Task BuyUntilAsync(string item, int count, bool force, CancellationToken ct);
    Task SellAsync(string item, CancellationToken ct);
    Task SellCategoryAsync(string category, CancellationToken ct);
    Task StoreAsync(string item, CancellationToken ct);
    Task StoreAllAsync(string category, CancellationToken ct);
    Task TakeOutAsync(string item, bool force, CancellationToken ct);
    /// <summary>修理/特修/强行修理/强行特修。target 既可能是部位("衣服")也可能是物品名。</summary>
    Task RepairAsync(string target, string kind, bool force, string? equipTo, CancellationToken ct);

    Task SkillAsync(string skill, int x, int y, CancellationToken ct);
    Task AbandonTargetAsync(CancellationToken ct);
    Task PetAsync(string cmd, CancellationToken ct);
    /// <summary>成对的开关命令统一走这里:战斗/挖矿/拾物/躲避PKER/闪避/引怪/穿怪/穿人/移动刺杀/编组/触发器/…</summary>
    Task FlagAsync(string flag, bool on, CancellationToken ct);
    /// <summary>搜索怪物范围[N]格 / 拣取物品范围[N]格。</summary>
    Task RangeAsync(string which, int n, CancellationToken ct);
    Task GroupTalkAsync(string to, string text, CancellationToken ct);
    Task GroupModeAsync(string mode, CancellationToken ct);
    Task<IReadOnlyList<string>?> ReadScriptAsync(string name, CancellationToken ct);
    Task LogOutAsync(int ms, CancellationToken ct);
    Task SendAsync(string text, bool backdoor, CancellationToken ct);
    /// <summary>主控/被控那一套要靠多进程通信,单机版只在日志里留一句。</summary>
    Task IpcAsync(string verb, CancellationToken ct);
    void Unsupported(string verb, string raw);

    /// <summary>毫秒时钟。全局段是按时间轮询的(见 ScriptEngine.GlobalPollMs),所以时钟交给实现方:
    /// 真机用 TickCount64,离线自检用一个只被 等待 推进的假时钟,这样流水账才可复现。</summary>
    long NowMs { get; }
}

/// <summary>按天骥的执行模型跑脚本:
/// 主流程是一条扁平语句表(标签只是路标),跑到 <挂机脚本结束> 就回到 <挂机脚本开始> 再跑一轮,
/// 开头的语句(全局段之前)只在第一次经过时执行 —— 真脚本正是拿它做变量初始化;
/// 全局段是另一个轮询循环,每执行完一条主流程语句就跑一遍,它里面的 跳转到 直接改写主流程的位置
/// (真脚本的脱困就是这么触发的:全局判断卡图,然后 跳转到<D611脱困>)。</summary>
public sealed class ScriptEngine
{
    private readonly ScriptProgram _main;
    private readonly IScriptApi _api;
    private readonly Dictionary<string, (ScriptProgram Prog, int Pc)> _labels = new(StringComparer.Ordinal);
    private readonly List<(ScriptProgram Prog, int Pc)> _calls = new();
    private ScriptProgram _cur;
    private int _pc;

    /// <summary>每次改写 _pc 就自增一次。主循环拿它对账:全局段轮询(以及等待期间的轮询)可能已经把执行位置
    /// 跳走,这时主循环不能再按旧位置 +1,否则会从跳转目标的第二句开始跑。</summary>
    private int _moved;

    private bool _globalOn = true;
    private bool _inGlobal;
    private bool _stop;
    private long _nextGlobalAt;

    /// <summary>全局段两次轮询之间的最小间隔。天骥的全局脚本本来就是定时器驱动的轮询,不是每句都查:
    /// 没有这个间隔时,"跳转到 的标签没把触发条件关掉"的脚本会把主流程钉死在那一行原地打转,
    /// 看起来就像脚本卡住不动(真机那边还会因此每秒发上百个包)。</summary>
    public int GlobalPollMs { get; set; } = 500;

    /// <summary>两轮之间(回到开头重跑、或 跳转到 往回跳)的最小间隔,0=不限速。
    /// 脚本一句 等待 都不写时,这是唯一的刹车:真机上有一条脚本在 doorlink 查不到门点时
    /// 以每秒上千轮空转,每轮两三行日志,把日志和 UI 的调度队列一起堆爆。
    /// 离线自检(ScriptCheck)的时钟只被 等待 推进、流水账要能逐行复现,所以那里保持默认 0。</summary>
    public int MinPassMs { get; set; }

    /// <summary>刚过了一轮的分界,下一圈主循环该补刹车。时间是假时钟上也取得到的时刻;
    /// 单独一个 bool 是因为 0 在离线自检的假时钟上是合法时刻。</summary>
    private bool _paceOwed;
    private long _passBoundaryAt;

    private void MarkPassBoundary()
    {
        _paceOwed = true;
        _passBoundaryAt = _api.NowMs;
    }

    public ScriptVars Vars { get; } = new();

    /// <summary>WPF 用它显示"现在跑到第几行/哪个标签"。</summary>
    public event Action<ScriptNode>? Stepped;
    public event Action<string>? Log;
    public string CurrentLabel { get; private set; } = string.Empty;
    public bool GlobalEnabled => _globalOn;

    public ScriptEngine(ScriptProgram main, IScriptApi api)
    {
        _main = _cur = main;
        _api = api;
        Vars.Src = api.Src;
        Index(main);
    }

    private void Index(ScriptProgram p)
    {
        foreach (var (name, i) in p.Labels)
            // 后装入的脚本不覆盖已有标签:真脚本里 <回城> 这类名字每个文件都想用。
            _labels.TryAdd(name, (p, i));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_main.Triggers.Count > 0)
            Log?.Invoke($"[脚本] 登记触发器 {_main.Triggers.Count} 个({string.Join(" ", _main.Triggers.Select(t => t.Name))}):" +
                        "这些块要等别的客户端发来匹配的 组队通信 才跑,单机没有对端,块里的命令一次都不会执行");
        _pc = 0;
        while (!ct.IsCancellationRequested && !_stop)
        {
            if (_cur == _main && _pc == _main.GlobalStart) _pc = _main.EntryPoint;
            if (_pc >= _cur.Body.Count)
            {
                if (_calls.Count > 0) { ReturnToCaller(); continue; }
                // 调用的模块漏写 返回、或者主流程跑到底:都回到挂机段开头重跑一轮。
                MarkPassBoundary();
                _cur = _main;
                _pc = _main.EntryPoint;
                continue;
            }
            ScriptNode n = _cur.Body[_pc];
            int before = _moved;
            bool moved = await RunNodeAsync(n, ct);
            if (!moved && before == _moved) _pc++;
            if (!_inGlobal) await RunGlobalPassAsync(ct);
            await PaceIfNeededAsync(ct);
        }
        Log?.Invoke($"[脚本] {(_stop ? "已停止" : "已取消")}");
    }

    private async Task<bool> RunNodeAsync(ScriptNode n, CancellationToken ct)
    {
        if (n.Kind == NodeKind.Label) { CurrentLabel = n.Name; return false; }
        Stepped?.Invoke(n);
        switch (n.Kind)
        {
            case NodeKind.Assign:
                Vars.Set(n.Name, n.Value!.Eval(Vars, _api.Src));
                return false;
            case NodeKind.If:
                var list = n.Cond!.Eval(Vars, _api.Src) ? n.Then : n.Else;
                foreach (ScriptNode child in list)
                    if (await RunNodeAsync(child, ct)) return true;
                return false;
            default:
                return await RunCommandAsync(n, ct);
        }
    }

    private async Task<bool> RunCommandAsync(ScriptNode node, CancellationToken ct)
    {
        ScriptCommand c = node.Cmd!;
        string verb = c.Verb;
        switch (verb)
        {
            case "跳转到": return Jump(c.Angle, clearCalls: true);
            case "调用":
                _calls.Add((_cur, _pc + 1));
                return Jump(c.Angle, clearCalls: false);
            case "返回":
                // 调用栈空着还 返回:回到开头,和跑到底一样是"过了一轮"的分界。
                if (_calls.Count == 0) MarkPassBoundary();
                ReturnToCaller();
                return true;
            case "停止脚本": _stop = true; Log?.Invoke($"[脚本] 第{c.Line}行 停止脚本"); return true;
            case "开启全局脚本": _globalOn = true; return false;
            case "关闭全局脚本": _globalOn = false; return false;

            case "等待": await WaitAsync(Int(A(c, 0)), ct); return false;
            case "系统显示": _api.Display(A(c, 0)); return false;
            case "说话": case "通告": await _api.SayAsync(A(c, 0), ct); return false;
            case "发送命令": await _api.SendAsync(A(c, 0), false, ct); return false;
            case "后门命令": await _api.SendAsync(A(c, 0), true, ct); return false;
            case "装入脚本": return await LoadAsync(A(c, 0), ct);

            case "走到": return await WalkAsync(c, fight: false, near: false, ct);
            case "走到附近": return await WalkAsync(c, fight: false, near: true, ct);
            case "走到跟前": return await WalkAsync(c, fight: false, near: true, ct);      // 天骥写到目标旁边
            case "边打边走到": return await WalkAsync(c, fight: true, near: false, ct);
            case "边打边走到附近": return await WalkAsync(c, fight: true, near: true, ct);
            case "走到NPC附近": await _api.WalkToNpcAsync(Npc(A(c, 0)), "", 0, 0, ct); return false;
            case "走到门点": return await DoorAsync(c, fight: false, ct);
            case "边打边走到门点": return await DoorAsync(c, fight: true, ct);
            case "随机移动":
                if (Triad(A(c, 0), out string rm, out int rx, out int ry)) await _api.RandomMoveAsync(rm, rx, ry, ct);
                return false;
            case "随机移动停止": await _api.RandomMoveStopAsync(ct); return false;
            case "传送":
                if (Triad(A(c, 0), out string _, out int tx, out int ty)) await _api.TeleportAsync(tx, ty, ct);
                return false;

            case "找到NPC":
                if (NpcSpot(A(c, 0), out string npc, out string fm, out int fx, out int fy))
                {
                    if (fm.Length == 0) fm = _api.Src.MapCode;
                    await _api.FindNpcAsync(npc, fm, fx, fy, ct);
                }
                return false;
            case "对话": await _api.TalkNpcAsync(A(c, 0), ct); return false;
            case "对话坐标":
                if (Triad(A(c, 0), out string _, out int dx, out int dy)) await _api.TalkNpcAtAsync(dx, dy, ct);
                return false;
            case "攻击NPC": await _api.AttackNpcAsync(A(c, 0), Int(Tail(c)), ct); return false;
            case "攻击坐标":
                if (Triad(A(c, 0), out string _, out int ax, out int ay)) await _api.AttackAtAsync(ax, ay, ct);
                return false;
            case "选择": case "选择_加强": await _api.ChooseAsync(A(c, 0), verb.Length > 2, ct); return false;
            case "选择位置": await _api.ChooseAtAsync(Int(A(c, 0)), ct); return false;

            case "使用": await _api.UseItemAsync(A(c, 0), ct); return false;
            case "装备": await _api.EquipAsync(A(c, 0), A(c, 1), ct); return false;
            case "卸下": await _api.TakeOffAsync(A(c, 0), ct); return false;
            case "丢弃": await _api.DropAsync(A(c, 0), Int(Tail(c)), ct); return false;
            case "丢弃金币": await _api.DropGoldAsync(Int(A(c, 0)), ct); return false;
            case "买够": await _api.BuyUntilAsync(A(c, 0), Int(Tail(c)), false, ct); return false;
            case "强行买够": await _api.BuyUntilAsync(A(c, 0), Int(Tail(c)), true, ct); return false;
            // "购[..]""取[..]" 是天骥旧版手册里的简写(命令函数变量.md 的"补充(来自旧数据)"段),老脚本会这么写
            case "购买": case "购": await _api.BuyAsync(A(c, 0), Int(Tail(c)), Slot(c), Repair(c), false, ct); return false;
            case "强行购买": await _api.BuyAsync(A(c, 0), Int(Tail(c)), Slot(c), Repair(c), true, ct); return false;
            case "卖物": await _api.SellAsync(A(c, 0), ct); return false;
            case "自动售物": await _api.SellCategoryAsync(A(c, 0), ct); return false;
            case "存物": await _api.StoreAsync(A(c, 0), ct); return false;
            case "自动存物": await _api.StoreAllAsync(A(c, 0), ct); return false;
            case "取物": case "取": await _api.TakeOutAsync(A(c, 0), false, ct); return false;
            case "强行取物": await _api.TakeOutAsync(A(c, 0), true, ct); return false;
            case "修": case "修理": await _api.RepairAsync(A(c, 0), "修理", false, Slot(c), ct); return false;
            case "特修": await _api.RepairAsync(A(c, 0), "特修", false, Slot(c), ct); return false;
            case "强行修理": await _api.RepairAsync(A(c, 0), "修理", true, Slot(c), ct); return false;
            case "强行特修": await _api.RepairAsync(A(c, 0), "特修", true, Slot(c), ct); return false;

            case "使用技能":
                if (c.ArgCount > 1 && Triad(A(c, 1), out string _, out int sx, out int sy)) await _api.SkillAsync(A(c, 0), sx, sy, ct);
                else await _api.SkillAsync(A(c, 0), 0, 0, ct);
                return false;
            case "放弃攻击目标": await _api.AbandonTargetAsync(ct); return false;
            case "宝宝攻击": await _api.PetAsync("攻击", ct); return false;
            case "宝宝休息": await _api.PetAsync("休息", ct); return false;
            case "组队通信": await _api.GroupTalkAsync(A(c, 0), A(c, 1), ct); return false;
            case "组队模式": await _api.GroupModeAsync(A(c, 0), ct); return false;
            case "小退": await _api.LogOutAsync(Int(A(c, 0)) * 1000, ct); return false;

            case "开始战斗": await _api.FlagAsync("战斗", true, ct); return false;
            case "停止战斗": case "战斗关闭": await _api.FlagAsync("战斗", false, ct); return false;
            case "战斗开启": await _api.FlagAsync("战斗", true, ct); return false;
            case "开始挖矿": await _api.FlagAsync("挖矿", true, ct); return false;
            case "停止挖矿": await _api.FlagAsync("挖矿", false, ct); return false;
            case "拣物开启": await _api.FlagAsync("拾物", true, ct); return false;
            case "拣物关闭": await _api.FlagAsync("拾物", false, ct); return false;
            case "打开编组": await _api.FlagAsync("编组", true, ct); return false;
            case "关闭编组": await _api.FlagAsync("编组", false, ct); return false;
            case "搜索怪物范围": await _api.RangeAsync("搜索怪物", Int(A(c, 0)), ct); return false;
            case "拣取物品范围": await _api.RangeAsync("拣取物品", Int(A(c, 0)), ct); return false;
            case "开始控制": case "结束控制": case "请求被控": case "解除被控":
            case "等待出发": case "请求出发": case "打开策略": case "关闭策略":
            case "关闭所有策略": case "合成命令": case "开启NPC对话校验":
            case "播放声音": case "触发器开启": case "触发器关闭":
                await _api.IpcAsync(verb, ct); return false;
        }

        if (verb.EndsWith("开启", StringComparison.Ordinal) && verb.Length > 2)
        { await _api.FlagAsync(verb[..^2], true, ct); return false; }
        if (verb.EndsWith("关闭", StringComparison.Ordinal) && verb.Length > 2)
        { await _api.FlagAsync(verb[..^2], false, ct); return false; }

        _api.Unsupported(verb, c.Raw);
        return false;
    }

    private async Task RunGlobalPassAsync(CancellationToken ct)
    {
        if (!_globalOn || _main.GlobalStart < 0) return;
        long now = _api.NowMs;
        if (now < _nextGlobalAt) return;
        _nextGlobalAt = now + GlobalPollMs;
        // 两个段标记本身不进 Body,所以全局段正好是 [GlobalStart, GlobalEnd)。
        int end = _main.GlobalEnd > _main.GlobalStart ? _main.GlobalEnd : _main.Body.Count;
        _inGlobal = true;
        try
        {
            for (int i = _main.GlobalStart; i < end; i++)
            {
                if (ct.IsCancellationRequested || _stop) return;
                if (await RunNodeAsync(_main.Body[i], ct)) return;
            }
        }
        finally { _inGlobal = false; }
    }

    /// <summary>等待期间全局段还得继续轮询,否则卡图时要干等完整个 等待。</summary>
    private async Task WaitAsync(int ms, CancellationToken ct)
    {
        if (_inGlobal || !_globalOn || _main.GlobalStart < 0 || ms <= 0)
        {
            if (ms > 0) await _api.WaitAsync(ms, ct);
            return;
        }
        int left = ms;
        int seq = _moved;
        while (left > 0 && !ct.IsCancellationRequested && !_stop && seq == _moved)
        {
            int slice = Math.Min(200, left);
            left -= slice;
            await _api.WaitAsync(slice, ct);
            await RunGlobalPassAsync(ct);
        }
    }

    private async Task<bool> WalkAsync(ScriptCommand c, bool fight, bool near, CancellationToken ct)
    {
        if (Triad(A(c, 0), out string m, out int x, out int y)) await _api.WalkToAsync(m, x, y, fight, near, ct);
        return false;
    }

    private async Task<bool> DoorAsync(ScriptCommand c, bool fight, CancellationToken ct)
    {
        if (Triad(A(c, 0), out string m1, out int x1, out int y1)
            && Triad(A(c, 1), out string m2, out int x2, out int y2))
            await _api.DoorToDoorAsync(m1, x1, y1, m2, x2, y2, fight, ct);
        return false;
    }

    private async Task<bool> LoadAsync(string name, CancellationToken ct)
    {
        IReadOnlyList<string>? lines = await _api.ReadScriptAsync(name, ct);
        if (lines == null) { Log?.Invoke($"[脚本] 装入脚本[{name}] 失败:找不到脚本文件"); return false; }
        var p = ScriptParser.Parse(lines, name);
        foreach (string d in p.Diagnostics) Log?.Invoke($"[脚本] {name}: {d}");
        Index(p);
        Log?.Invoke($"[脚本] 已装入 {name}:{p.Body.Count} 条语句,{p.Labels.Count} 个标签");
        return false;
    }

    private bool Jump(string label, bool clearCalls)
    {
        if (label.Length == 0) { Log?.Invoke("[脚本] 跳转/调用缺少 <标签名>"); return false; }
        if (!_labels.TryGetValue(label, out var target))
        {
            // 找不到标签绝不能中断脚本:真角色少写一个标签是常事。
            Log?.Invoke($"[脚本] 没有标签 <{label}>,忽略这一句");
            return false;
        }
        // 往回跳就是脚本自己写的循环:这一轮到此为止,欠一次刹车(见 MinPassMs)。
        if (target.Prog == _cur && target.Pc <= _pc) MarkPassBoundary();
        if (clearCalls) _calls.Clear();
        _cur = target.Prog;
        _pc = target.Pc;
        _moved++;
        CurrentLabel = label;
        return true;
    }

    /// <summary>过了一轮的分界(跑回开头 / 往回 跳转到 / 空栈 返回)后补上 MinPassMs 的间隔。
    /// 不欠刹车时只有一次字段判断,正常脚本一点感觉都没有;一句 等待 都没写的脚本靠这一道刹车
    /// 从每秒上千轮降到每秒 1000/MinPassMs 轮。离线自检传 0,流水账因此一个字都不变。</summary>
    private async Task PaceIfNeededAsync(CancellationToken ct)
    {
        if (!_paceOwed) return;
        long at = _passBoundaryAt;
        _paceOwed = false;
        if (MinPassMs <= 0) return;
        long due = at + MinPassMs;
        long now = _api.NowMs;
        if (due > now) await _api.WaitAsync((int)(due - now), ct).ConfigureAwait(false);
    }

    private void ReturnToCaller()
    {
        if (_calls.Count == 0)
        {
            Log?.Invoke("[脚本] 返回 但调用栈是空的,回到挂机段开头");
            _cur = _main;
            _pc = _main.EntryPoint;
            _moved++;
            return;
        }
        (_cur, _pc) = _calls[^1];
        _calls.RemoveAt(_calls.Count - 1);
        _moved++;
    }

    /// <summary>命令参数在交给动作层之前一律先插值:购买[$衣服]装备到[衣服] 这种写法满脚本都是。</summary>
    private string A(ScriptCommand c, int i) => ScriptExpr.Interpolate(c.Bracket(i), Vars);
    private static string Tail(ScriptCommand c) => c.Literals.Count > 0 ? c.Literals[^1] : string.Empty;

    /// <summary>"N个"/"10次" 这种挂在参数后面的数量。没写就当 1。</summary>
    private static int Int(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '$')) i++;
        int j = i;
        while (j < s.Length && char.IsDigit(s[j])) j++;
        return j > i && int.TryParse(s[i..j], out int v) ? v : 0;
    }

    /// <summary>走到[3,330,330] / 传送到[308,219](省略地图=当前图)。参数残缺时返回 false,这一句直接跳过。</summary>
    private bool Triad(string s, out string map, out int x, out int y)
    {
        map = _api.Src.MapCode; x = y = 0;
        string[] parts = s.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        int i = parts.Length >= 3 ? 1 : 0;
        if (parts.Length >= 3) map = parts[0];
        return int.TryParse(parts[i], out x) && int.TryParse(parts[i + 1], out y);
    }

    /// <summary>找到NPC[流浪汉,3,346,334] 这种"名字和坐标挤在一个方括号里"的写法:
    /// 末尾两段永远是 X,Y,倒着第二段之前的非数字段是名字,名字前面那段是地图编号(可能是 3,也可能是 D611)。</summary>
    private static bool NpcSpot(string s, out string name, out string map, out int x, out int y)
    {
        name = map = string.Empty; x = y = 0;
        string[] p = s.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length < 2) return false;
        if (!int.TryParse(p[^2], out x) || !int.TryParse(p[^1], out y)) return false;
        if (p.Length >= 4) { name = p[0]; map = p[1]; }
        else if (p.Length == 3 && int.TryParse(p[0], out _)) map = p[0];
        else if (p.Length == 3) name = p[0];
        return true;
    }

    /// <summary>找到NPC[悦来客栈老板,3,305,373] 里 NPC 名和坐标挤在同一个方括号里。</summary>
    private static string Npc(string s)
    {
        int comma = s.IndexOf(',');
        return (comma > 0 ? s[..comma] : s).Trim();
    }

    /// <summary>"购买[X]装备到[Y]" 的第二段是部位;只有 修理[X] 这种一句一段时返回 null。</summary>
    private string? Slot(ScriptCommand c) => c.ArgCount >= 2 ? A(c, 1) : null;

    /// <summary>购买/修理家族里的 "强行特修后装备到" 这类修饰词,从裸文本里读。</summary>
    private static string? Repair(ScriptCommand c)
    {
        foreach (string lit in c.Literals)
        {
            if (lit.Contains("特修", StringComparison.Ordinal)) return "特修";
            if (lit.Contains("修理", StringComparison.Ordinal)) return "修理";
        }
        return null;
    }
}
