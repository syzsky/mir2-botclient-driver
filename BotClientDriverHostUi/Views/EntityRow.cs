namespace BotClientDriverHostUi.Views;

/// <summary>
/// 左侧列表（怪物 / NPC / 地面物品 / 玩家）的统一行模型。
/// 只用简单属性 + GridView 绑定，不引入 MVVM 框架。
/// </summary>
public sealed class EntityRow
{
    public EntityRow(string name, int distance, int x, int y, string extra = "")
    {
        Name = string.IsNullOrWhiteSpace(name) ? "（未知）" : name;
        Distance = distance;
        Position = $"({x},{y})";
        Extra = extra;
    }

    public string Name { get; }

    public int Distance { get; }

    public string Position { get; }

    public string Extra { get; }
}
