namespace BotClient.Session;

/// <summary>
/// 物品过滤配置 — 自定义拾取/不拾取物品清单。
/// 支持按名称关键词匹配和按类别过滤。
/// </summary>
public sealed class ItemFilterConfig
{
    /// <summary>自动拾取开关</summary>
    public bool AutoPickup { get; set; } = true;

    /// <summary>拾取范围(格数)</summary>
    public int PickupRange { get; set; } = 10;

    /// <summary>拾取延迟(ms)</summary>
    public int PickupIntervalMs { get; set; } = 500;

    /// <summary>白名单:只拾取包含这些关键词的物品(空=拾取所有)。
    /// 药必须在这里:挂机喝药那条链是"先看背包里有什么药"(BotCombatAI.Potions),
    /// 打怪掉的药不捡进背包,血掉下去就没得喝 —— 真机踩过这一对互相矛盾的配置。</summary>
    public readonly List<string> PickupKeywords = new()
    {
        "金币", "金条", "金砖",
        "金创药", "魔法药",
        "太阳水", "疗伤药", "万年雪霜",
        "祝福油", "罗刹",
        "天甲", "天神", "梦幻", "嗜魂",
        "手镯", "戒指", "项链", "头盔",
        "腰带", "靴子", "宝石", "勋章",
        "裁决", "骨玉", "龙纹", "屠龙",
        "书", "技能"
    };

    /// <summary>黑名单:不拾取的物品关键词。
    /// 这里原先写着排除 "金创药(小)"/"魔法药(小)" —— 本服(老底板)低级怪只掉这两档药,
    /// 等于把唯一的补给源拉黑;新手村掉的 布衣/木剑 才是该排除的垃圾。</summary>
    public readonly List<string> IgnoreKeywords = new()
    {
        "布衣", "木剑", "蜡烛",
        "铁剑", "青铜", "钢手镯", "铁手镯",
        "古铜戒指", "玻璃戒指", "六角戒"
    };

    /// <summary>检查物品名是否允许拾取</summary>
    public bool ShouldPickup(string itemName)
    {
        if (string.IsNullOrEmpty(itemName)) return false;

        // 黑名单检查
        foreach (var kw in IgnoreKeywords)
        {
            if (itemName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // 白名单检查(如果有白名单规则)
        if (PickupKeywords.Count > 0)
        {
            foreach (var kw in PickupKeywords)
            {
                if (itemName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false; // 不在白名单中,不拾取
        }

        return true; // 无白名单规则,全拾取
    }
}
