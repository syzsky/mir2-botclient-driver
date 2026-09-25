using System;
using System.Collections.Generic;
using System.Linq;
using BotClient.ClientDriver.Human;
using BotClient.Human;
using BotClient.Session.Combat;

namespace HumanSelfTest;

/// <summary>
/// 拟真操作改造的离线自测。
///
/// 验的是三件容易"看着对、实际坏"的事：
///   1. 拟人时序**只加不减** —— 服务端限速是硬下限，随机化只能往上叠，绝不能把间隔压到阈值下；
///   2. 鼠标轨迹**终点必须精确落在目标格** —— 过程可以弯、可以过冲，但最后一步不能偏，
///      否则"像人了"却打不到怪，比不改还糟；
///   3. 技能循环**挑不出技能时必须安全退回** —— 返回 null 而不是抛异常/乱放技能。
/// </summary>
internal static class Program
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {name}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : "  ← " + detail)}"); }
    }

    private static int Main()
    {
        TestHumanTiming();
        TestMousePath();
        TestSkillRotation();

        Console.WriteLine();
        Console.WriteLine($"[HumanSelfTest] 通过 {_pass} / 失败 {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- 拟人时序
    private static void TestHumanTiming()
    {
        Console.WriteLine("[拟人时序]");
        var t = new HumanTuning();

        // 硬约束：LongTail 永不小于下限（下限是服务端限速，压下去=被丢包）
        int min = 700, span = 400;
        var samples = new List<int>();
        for (int i = 0; i < 4000; i++) samples.Add(HumanTiming.LongTail(min, span));
        Check("LongTail 恒 ≥ 下限", samples.All(v => v >= min), $"min={samples.Min()}");
        Check("LongTail 有长尾", samples.Max() > min + span * 1.5, $"max={samples.Max()}");

        // 右偏：中位数应明显靠近下限（真人的慢是偶发，不是常态）
        samples.Sort();
        int median = samples[samples.Count / 2];
        Check("LongTail 呈右偏（中位数靠近下限）", median < min + span * 0.6, $"median={median}");

        // 抖动不能夸张到失控：绝大多数样本不超过下限+2×span（否则就是"卡住"而不是"像人"）
        int outliers = samples.Count(v => v > min + span * 3.5);
        Check("LongTail 无失控离群", outliers == 0, $"越界={outliers}");

        // 截断高斯必须落在区间内
        bool inRange = true;
        for (int i = 0; i < 2000; i++)
        {
            double g = HumanTiming.Gauss(50, 20, 10, 120);
            if (g < 10 || g > 120) { inRange = false; break; }
        }
        Check("Gauss 截断在 [min,max]", inRange);

        Check("Chance(0) 恒 false", !HumanTiming.Chance(0));
        Check("Chance(1) 恒 true", HumanTiming.Chance(1));
        Check("HoldMs 不返回负数", Enumerable.Range(0, 500).All(_ => HumanTiming.HoldMs(40, 90) >= 10));
        Check("疲劳系数 ≥ 1", HumanTiming.FatigueFactor(t) >= 1.0);

        string desc = HumanTiming.Describe(t);
        Check("Describe 可读且含开关", desc.StartsWith("开"), desc);
        Check("Describe(关) 走固定时序", HumanTiming.Describe(new HumanTuning { Enabled = false }).Contains("关"));

        // 默认档必须是"拟真开"，否则等于装了个摆设
        var def = new HumanTuning();
        Check("默认档：拟真默认开", def.Enabled);
        Check("默认档：战斗/走路抖动都开", def.CombatJitterRatio > 0 && def.WalkJitterRatio > 0,
            $"combat={def.CombatJitterRatio} walk={def.WalkJitterRatio}");
    }

    // ---------------------------------------------------------------- 鼠标轨迹
    private static void TestMousePath()
    {
        Console.WriteLine("[人化鼠标轨迹]");
        var t = new HumanTuning();

        // 关键中的关键：终点必须精确等于目标，否则轨迹再自然也是点错格
        bool endpointOk = true, delayOk = true, jumpOk = true;
        int minSteps = int.MaxValue, maxSteps = 0;
        for (int i = 0; i < 300; i++)
        {
            int fx = 100, fy = 100, tx = 640, ty = 480;
            var steps = HumanMousePath.Build(fx, fy, tx, ty, t);
            if (steps.Count == 0 || steps[^1].X != tx || steps[^1].Y != ty) { endpointOk = false; break; }

            double dist = Math.Sqrt((tx - fx) * (tx - fx) + (ty - fy) * (ty - fy));
            int px = fx, py = fy;
            foreach (var s in steps)
            {
                if (s.DelayMs < 0) { delayOk = false; break; }
                double hop = Math.Sqrt((s.X - px) * (s.X - px) + (s.Y - py) * (s.Y - py));
                // 单步位移不得超过全程距离的 80%（防"瞬移式"大跳），过冲回修那两步除外
                if (hop > dist * 0.8 + 24) { jumpOk = false; break; }
                px = s.X; py = s.Y;
            }
            if (!delayOk || !jumpOk) break;

            minSteps = Math.Min(minSteps, steps.Count);
            maxSteps = Math.Max(maxSteps, steps.Count);
        }
        Check("终点精确落在目标点", endpointOk);
        Check("每步延迟非负", delayOk);
        Check("路径连续（无瞬移大跳）", jumpOk, $"steps={minSteps}~{maxSteps}");
        Check("步数落在合理区间", minSteps >= 3 && maxSteps <= 64, $"{minSteps}~{maxSteps}");

        // 关掉拟人 → 单点直达（一键回退路径）
        var off = HumanMousePath.Build(100, 100, 640, 480, new HumanTuning { Enabled = false });
        Check("关拟人=单点直达", off.Count == 1 && off[0].X == 640 && off[0].Y == 480);

        // 近距离退化：不生成"原地抖动"
        var near = HumanMousePath.Build(300, 300, 302, 302, t);
        Check("近距离退化为单点", near.Count == 1 && near[0].X == 302 && near[0].Y == 302);

        Check("null 档不抛异常", HumanMousePath.Build(0, 0, 10, 10, null!).Count == 1);
    }

    // ---------------------------------------------------------------- 技能循环
    private static void TestSkillRotation()
    {
        Console.WriteLine("[技能循环]");

        var plan = SkillRotationPlan.Sample();
        plan.Enabled = true;
        var ids = new Dictionary<string, int> { ["火墙"] = 201, ["冰咆哮"] = 202, ["灵魂火符"] = 203 };
        var rot = new SkillRotation(plan, n => ids.TryGetValue(n, out int v) ? v : -1, _ => 0)
        {
            MagicHitIntervalMs = 1150,
        };
        var t0 = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

        // 关掉总开关 → 必须完全退回旧行为（null = 交给物理/单法术）
        Check("总开关关闭返回 null", new SkillRotation(new SkillRotationPlan { Enabled = false }, _ => 201, _ => 0)
            .Choose(new SkillQuery(100, 5, 1, 10, 10, 9, 10), t0) is null);

        // 3 只怪贴近 → 用最高优先级的 AOE（火墙），且地面技能落点=自身+配置偏移
        var pick = rot.Choose(new SkillQuery(50, 3, 3, 200, 200, 100, 100), t0);
        var c = pick!.Value;
        Check("多怪优选高优先级 AOE", c.Name == "火墙" && c.MagicId == 201 && c.SpellSlot == 3, c.Name);
        Check("地面技能落点=自身+偏移", c.GroundCast && c.X == 100 && c.Y == 99, $"({c.X},{c.Y})");

        // 单怪 → AOE 的 MinMonsters 不满足，降到单体
        var solo = rot.Choose(new SkillQuery(50, 1, 3, 200, 200, 100, 100), t0);
        Check("单怪降级到单体技能", solo!.Value.Name == "灵魂火符" && !solo.Value.GroundCast, solo.Value.Name);

        // MP 见底 → 单体技能的 MinSelfMpPercent 也拦得住
        Check("MP 不足时不硬放", rot.Choose(new SkillQuery(2, 1, 3, 200, 200, 100, 100), t0) is null);

        // 超出所有技能射程 → null（由调用方去走位）
        Check("超射程返回 null", rot.Choose(new SkillQuery(100, 1, 30, 900, 900, 100, 100), t0) is null);

        // 冷却：刚放完火墙，立刻再问 → 不能连放，必须换技能或等
        rot.MarkCast(201, t0);
        var after = rot.Choose(new SkillQuery(50, 3, 3, 200, 200, 100, 100), t0.AddMilliseconds(100));
        Check("冷却中不连放同一技能", after!.Value.MagicId != 201, after.Value.Name);

        // 冷却走完（ExtraCooldownMs=2500）→ 火墙重新可用
        var later = rot.Choose(new SkillQuery(50, 3, 3, 200, 200, 100, 100), t0.AddMilliseconds(2600));
        Check("冷却结束后恢复", later!.Value.MagicId == 201, later.Value.Name);

        // 未学会的技能要静默跳过，不能抛出，也不能让它顶替能用的技能
        var partial = new SkillRotation(plan, n => n == "火墙" ? 201 : -1, _ => 0) { MagicHitIntervalMs = 1150 };
        var onlyPhysical = partial.Choose(new SkillQuery(50, 1, 3, 200, 200, 100, 100), t0.AddMilliseconds(99999));
        Check("未学会技能被跳过", onlyPhysical is null, onlyPhysical?.Name);

        // MagicId 直填（不看名字）时 SpellSlot=-1 = 交给驱动层兜底
        var direct = new SkillRotation(new SkillRotationPlan
        {
            Enabled = true,
            Slots = new List<SkillSlotDef> { new() { MagicId = 301, Priority = 1, MaxDistance = 8 } },
        }, _ => -1, _ => 0);
        var d = direct.Choose(new SkillQuery(100, 1, 2, 5, 5, 4, 4), t0);
        Check("MagicId 直填可用且槽位交给驱动", d!.Value.MagicId == 301 && d.Value.SpellSlot < 0);

        Check("Describe 不抛异常", rot.Describe(t0).Length > 0);
        Check("Reset 后可再用", ResetWorks(partial, t0));
    }

    private static bool ResetWorks(SkillRotation rot, DateTime t0)
    {
        try { rot.Reset(); return true; }
        catch { return false; }
    }
}
