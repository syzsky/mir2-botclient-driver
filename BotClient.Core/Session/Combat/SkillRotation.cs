using System;
using System.Collections.Generic;
using System.Linq;

namespace BotClient.Session.Combat;

/// <summary>
/// 一个技能槽（技能循环的最小单元）。所有字段都可从 JSON 配置，换职业换服不用改代码。
/// </summary>
public sealed class SkillSlotDef
{
    public bool Enabled { get; set; } = true;

    /// <summary>技能名（与服务端 SM_SENDMYMAGIC 下发的 sMagicName 做包含匹配）。MagicId &gt; 0 时不看本字段。</summary>
    public string Name { get; set; } = "";

    /// <summary>魔法 ID。&gt; 0 直接用它；= 0 时按 Name 从已学技能列表里反查。</summary>
    public int MagicId { get; set; }

    /// <summary>法术槽（客户端魔法栏第几个，对应 Keys.SpellKeys 下标，0 = F1）。-1 = 用 MagicId 兜底推算。</summary>
    public int SpellSlot { get; set; } = -1;

    /// <summary>优先级，大的先选。</summary>
    public int Priority { get; set; }

    /// <summary>视野内至少多少只怪才用（AOE 技能设 3 以上；单体技能保持 1）。</summary>
    public int MinMonsters { get; set; } = 1;

    /// <summary>目标距离上限（格）；0 = 不限。</summary>
    public int MaxDistance { get; set; } = 8;

    /// <summary>自身 MP 低于该百分比时不用（0 = 不限）。</summary>
    public int MinSelfMpPercent { get; set; }

    /// <summary>目标点释放：把落点放在自己脚下（火墙/地雷这类铺地魔法）。</summary>
    public bool GroundCast { get; set; }

    /// <summary>地面释放时的落点相对自身的格偏移（GroundCast=true 才生效，例如 0,-1 = 自己上方一格）。</summary>
    public int GroundOffsetX { get; set; }
    public int GroundOffsetY { get; set; }

    /// <summary>额外冷却（毫秒）。与服务端 Magic.DB 的 Delay 取较大值，用来给"服务端不管但你不想连放"的技能限流。</summary>
    public int ExtraCooldownMs { get; set; }

    public string Describe()
        => $"{Name}{(MagicId > 0 ? $"(id={MagicId})" : "")} 槽{(SpellSlot >= 0 ? SpellSlot.ToString() : "auto")} " +
           $"P{Priority} 怪≥{MinMonsters} 距≤{MaxDistance} MP≥{MinSelfMpPercent}%{(GroundCast ? " 地面" : "")}" +
           $"{(ExtraCooldownMs > 0 ? $" +CD{ExtraCooldownMs}" : "")}";
}

/// <summary>技能循环配置（整段进 clientdriver.json / botsettings.json）。</summary>
public sealed class SkillRotationPlan
{
    /// <summary>总开关。false = 退回到"单一 MagicId 打天下"的旧行为。</summary>
    public bool Enabled { get; set; }

    /// <summary>技能表。按 Priority 从高到低依次尝试，冷却未到/条件不满足就跳到下一个，全不满足则物理攻击。</summary>
    public List<SkillSlotDef> Slots { get; set; } = new();

    /// <summary>给一份"法师单体+群体"的可直接照抄的示例（写进 json 后被用户改写）。</summary>
    public static SkillRotationPlan Sample() => new()
    {
        Enabled = false,   // 默认关：没配技能表的号行为与改造前完全一致
        Slots = new List<SkillSlotDef>
        {
            new() { Name = "火墙",       SpellSlot = 3, Priority = 30, MinMonsters = 3, MaxDistance = 6, MinSelfMpPercent = 30, GroundCast = true, GroundOffsetY = -1, ExtraCooldownMs = 2500 },
            new() { Name = "冰咆哮",     SpellSlot = 2, Priority = 20, MinMonsters = 2, MaxDistance = 7, MinSelfMpPercent = 20, ExtraCooldownMs = 800 },
            new() { Name = "灵魂火符",   SpellSlot = 0, Priority = 10, MinMonsters = 1, MaxDistance = 8, MinSelfMpPercent = 5 },
        },
    };
}

/// <summary>决策当次快照（由战斗 AI 每轮填，纯数据，便于离线自测）。</summary>
public readonly record struct SkillQuery(
    int SelfMpPercent,
    int VisibleMonsters,
    int TargetDistance,
    int TargetX,
    int TargetY,
    int SelfX,
    int SelfY);

/// <summary>选中的技能。</summary>
public readonly record struct SkillChoice(
    int MagicId,
    int SpellSlot,
    string Name,
    bool GroundCast,
    int X,
    int Y);

/// <summary>
/// 技能循环决策器（纯逻辑，无 IO，可离线自测）。
///
/// 它解决的问题：改造前的挂机只有"一个 MagicId 死磕"，法师不会按场面切技能。
/// 现在每次攻击前先问它一遍：按优先级从高到低找第一个"条件满足且冷却已到"的技能；
/// 一个都没有 → 返回 null，由调用方退回物理攻击。
///
/// 冷却的下限来自服务端（Magic.DB 的 Delay + MagicHitIntervalTime），**不允许低于它**——
/// 这跟改造前是同一条硬约束，技能循环只是在此之上做"什么时候用哪个"，不放松限速。
/// </summary>
public sealed class SkillRotation
{
    private readonly SkillRotationPlan _plan;
    private readonly Func<string, int> _resolveByName;
    private readonly Func<int, int> _serverDelayMs;
    private readonly Dictionary<int, DateTime> _lastCast = new();

    /// <summary>与服务端 MagicHitIntervalTime 对齐的附加间隔（默认 1150）。</summary>
    public int MagicHitIntervalMs { get; set; } = 1150;

    public SkillRotation(SkillRotationPlan plan, Func<string, int> resolveByName, Func<int, int> serverDelayMs)
    {
        _plan = plan ?? new SkillRotationPlan();
        _resolveByName = resolveByName ?? (_ => -1);
        _serverDelayMs = serverDelayMs ?? (_ => 0);
    }

    public bool Enabled => _plan.Enabled;

    /// <summary>当前生效的技能表（供日志/自检展示）。</summary>
    public IReadOnlyList<SkillSlotDef> Slots => _plan.Slots;

    /// <summary>
    /// 选技能。<paramref name="now"/> 传入当前 UTC 时间（由调用方统一取，避免各处 DateTime.UtcNow 不一致）。
    /// </summary>
    public SkillChoice? Choose(in SkillQuery q, DateTime now)
    {
        if (!_plan.Enabled) return null;

        SkillSlotDef? best = null;
        int bestPri = int.MinValue;
        int bestIndex = int.MaxValue;
        int bestMagicId = 0;
        int bestSlot = -1;

        for (int i = 0; i < _plan.Slots.Count; i++)
        {
            var s = _plan.Slots[i];
            if (s is null || !s.Enabled) continue;

            int magicId = s.MagicId > 0 ? s.MagicId : (string.IsNullOrWhiteSpace(s.Name) ? -1 : _resolveByName(s.Name));
            if (magicId <= 0) continue;                                  // 没学会/名字不对 → 跳过，不报错
            if (q.VisibleMonsters < s.MinMonsters) continue;
            if (s.MaxDistance > 0 && q.TargetDistance > s.MaxDistance) continue;
            if (s.MinSelfMpPercent > 0 && q.SelfMpPercent < s.MinSelfMpPercent) continue;

            int cd = Math.Max(_serverDelayMs(magicId) + MagicHitIntervalMs, s.ExtraCooldownMs);
            if (_lastCast.TryGetValue(magicId, out var last) &&
                (now - last).TotalMilliseconds < cd) continue;

            if (s.Priority > bestPri || (s.Priority == bestPri && i < bestIndex))
            {
                best = s; bestPri = s.Priority; bestIndex = i;
                bestMagicId = magicId;
                bestSlot = s.SpellSlot >= 0 ? s.SpellSlot : -1;
            }
        }

        if (best is null) return null;

        int x = q.TargetX, y = q.TargetY;
        if (best.GroundCast)
        {
            x = q.SelfX + best.GroundOffsetX;
            y = q.SelfY + best.GroundOffsetY;
        }
        string name = string.IsNullOrWhiteSpace(best.Name) ? $"魔法#{bestMagicId}" : best.Name;
        return new SkillChoice(bestMagicId, bestSlot, name, best.GroundCast, x, y);
    }

    /// <summary>记录一次成功施法（用于冷却计时）。</summary>
    public void MarkCast(int magicId, DateTime now)
    {
        if (magicId > 0) _lastCast[magicId] = now;
    }

    /// <summary>清空冷却（重连/换图后调用）。</summary>
    public void Reset() => _lastCast.Clear();

    /// <summary>诊断用：当前技能表 + 剩余冷却。</summary>
    public string Describe(DateTime now)
    {
        if (!_plan.Enabled) return "关";
        if (_plan.Slots.Count == 0) return "开（技能表为空）";
        var parts = new List<string>();
        foreach (var s in _plan.Slots.Where(s => s.Enabled))
        {
            int id = s.MagicId > 0 ? s.MagicId : (string.IsNullOrWhiteSpace(s.Name) ? -1 : _resolveByName(s.Name));
            string cd = "就绪";
            if (id > 0 && _lastCast.TryGetValue(id, out var last))
            {
                int total = Math.Max(_serverDelayMs(id) + MagicHitIntervalMs, s.ExtraCooldownMs);
                int left = total - (int)(now - last).TotalMilliseconds;
                if (left > 0) cd = $"CD{left}ms";
            }
            parts.Add($"{s.Describe()}[{(id <= 0 ? "未学会" : cd)}]");
        }
        return string.Join(" | ", parts);
    }
}
