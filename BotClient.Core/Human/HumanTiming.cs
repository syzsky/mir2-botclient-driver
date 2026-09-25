using System;
using System.Threading;

namespace BotClient.Human;

/// <summary>
/// 拟人化参数（跨层共享：Core 的战斗节奏 + ClientDriver 的鼠标/键盘操作都读它）。
///
/// 设计原则（重要，别把"像人"做成"比人慢"）：
///   ① 所有拟人化只做**加法**：在服务端限速下限之上叠加随机冗余，绝不把间隔压到阈值以下——
///      下限是硬约束（压下去会被整包丢弃），随机只能往上加。
///   ② 时间分布用"右偏长尾"而不是均匀分布：真人的操作延迟绝大多数很快、偶尔明显停顿，
///      均匀分布的等距脉冲恰恰是最容易被统计出来的机器特征。
///   ③ 允许关（Enabled=false）→ 退回改造前的固定行为，便于出问题时一键回退对比。
/// </summary>
public sealed class HumanTuning
{
    /// <summary>总开关。false = 退回改造前的固定时序（用于对照与排障）。</summary>
    public bool Enabled { get; set; } = true;

    // ---------------- 鼠标轨迹 ----------------
    /// <summary>一次移动拆成多少个小步（真人拖动鼠标是连续采样，不是瞬移）。</summary>
    public int TrajectoryStepsMin { get; set; } = 6;
    public int TrajectoryStepsMax { get; set; } = 18;

    /// <summary>每个小步之间的间隔（毫秒）。真人的鼠标采样间隔大约 5~15ms。</summary>
    public int StepDelayMinMs { get; set; } = 4;
    public int StepDelayMaxMs { get; set; } = 13;

    /// <summary>路径弯曲程度：控制点相对直线垂距的随机比例（真人手不会走直线）。</summary>
    public double CurveBias { get; set; } = 0.35;

    /// <summary>过冲概率：先滑过头一点再回修（真人常见动作特征）。</summary>
    public double OvershootChance { get; set; } = 0.18;
    public int OvershootPx { get; set; } = 14;

    // ---------------- 停顿 ----------------
    /// <summary>动作前偶发长停顿的概率（看手机/走神/犹豫）。</summary>
    public double PauseChance { get; set; } = 0.06;
    public int PauseMinMs { get; set; } = 400;
    public int PauseMaxMs { get; set; } = 2200;

    // ---------------- 疲劳 ----------------
    /// <summary>连续运行越久，停顿系数越大（真人越挂越迟钝）。只抬高停顿，不降低其它限速。</summary>
    public bool FatigueEnabled { get; set; } = true;
    public double FatigueFullHours { get; set; } = 3.0;
    public double FatigueMaxFactor { get; set; } = 1.6;

    // ---------------- 战斗节奏 ----------------
    /// <summary>攻击/施法间隔在下限之上再叠加的随机比例上限（0 = 完全固定，0.25 = 最多多等 25%）。</summary>
    public double CombatJitterRatio { get; set; } = 0.25;

    /// <summary>走路间隔（两次点地图格之间）在下限之上叠加的随机比例上限。
    /// 真人赶路是一阵快一阵慢，等距脉冲的"每 400ms 一格"本身就很显眼。</summary>
    public double WalkJitterRatio { get; set; } = 0.20;

    /// <summary>视野无怪时的发呆概率（原地稍等，比机械地反复挪位置更像真人）。</summary>
    public double IdleWanderChance { get; set; } = 0.12;
    public int IdlePauseMinMs { get; set; } = 300;
    public int IdlePauseMaxMs { get; set; } = 1500;
}

/// <summary>
/// 拟人时序生成器。全部是纯函数式随机，无外部依赖，方便离线自测。
///
/// 为什么不直接用 Random.Next(min,max)：
///   Next 是**均匀分布**，落在区间里的概率处处相等；真人操作延迟是**右偏分布**——
///   大量动作集中在很短的时间内完成，偶尔拖出一个长尾。均匀分布反而暴露机器特征。
///   这里用截断高斯 + 对数正态式长尾来生成。
/// </summary>
public static class HumanTiming
{
    private static readonly ThreadLocal<Random> _rng =
        new(() => new Random(unchecked(Environment.TickCount * 397 ^ Guid.NewGuid().GetHashCode())));

    private static readonly long _bornTicks = Environment.TickCount64;

    public static Random Rng => _rng.Value!;

    public static double NextDouble() => Rng.NextDouble();

    public static int Next(int minInclusive, int maxExclusive)
        => maxExclusive <= minInclusive ? minInclusive : Rng.Next(minInclusive, maxExclusive);

    public static bool Chance(double p) => p > 0 && Rng.NextDouble() < p;

    /// <summary>截断高斯（Box-Muller），结果夹在 [min,max] 内。</summary>
    public static double Gauss(double mean, double std, double min, double max)
    {
        if (std <= 0) return Math.Clamp(mean, min, max);
        double u1 = 1.0 - Rng.NextDouble();
        double u2 = 1.0 - Rng.NextDouble();
        double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return Math.Clamp(mean + z * std, min, max);
    }

    /// <summary>
    /// 右偏长尾延迟：返回值 ≥ <paramref name="minMs"/>，
    /// 绝大多数落在 min ~ min+span 内，少数拖到 2~3.5 倍（长尾）。
    /// </summary>
    public static int LongTail(int minMs, int typicalSpanMs)
    {
        int span = Math.Max(1, typicalSpanMs);
        double median = span * 0.42;                       // 中位数偏向小值 → 右偏
        double x = Math.Abs(Gauss(median, span * 0.55, 0, span * 3.5));
        return minMs + (int)Math.Round(x);
    }

    /// <summary>疲劳系数（≥1）。连续运行越久越大，用来放大停顿概率与时长。</summary>
    public static double FatigueFactor(HumanTuning t)
    {
        if (t is null || !t.FatigueEnabled) return 1.0;
        double hours = (Environment.TickCount64 - _bornTicks) / 3_600_000.0;
        double k = Math.Clamp(hours / Math.Max(0.1, t.FatigueFullHours), 0, 1);
        return 1.0 + Math.Max(0.0, t.FatigueMaxFactor - 1.0) * k;
    }

    /// <summary>偶发长停顿（同步 Sleep，供输入层在两次底层操作之间调用）。</summary>
    public static void MaybePause(HumanTuning t)
    {
        if (t is null || !t.Enabled) return;
        if (!Chance(t.PauseChance * FatigueFactor(t))) return;
        Thread.Sleep(Next(t.PauseMinMs, Math.Max(t.PauseMinMs + 1, t.PauseMaxMs + 1)));
    }

    /// <summary>按下时长：以配置区间为基准的高斯抖动。</summary>
    public static int HoldMs(int minMs, int maxMs, double fatigue = 1.0)
    {
        int lo = Math.Max(10, minMs);
        int hi = Math.Max(lo + 1, maxMs);
        double mean = (lo + hi) / 2.0;
        double std = Math.Max(4.0, (hi - lo) / 3.0);
        double v = Gauss(mean, std, lo, hi * 1.35) * fatigue;
        return (int)Math.Round(Math.Clamp(v, 10, hi * 2.0));
    }

    /// <summary>一句话描述当前拟人档（供启动日志/SelfCheck 打印）。</summary>
    public static string Describe(HumanTuning? t)
    {
        if (t is null || !t.Enabled) return "关（固定时序）";
        return $"开[轨迹{t.TrajectoryStepsMin}-{t.TrajectoryStepsMax}步/" +
               $"停顿{t.PauseChance:P0}/疲劳{(t.FatigueEnabled ? $"{t.FatigueMaxFactor:0.0}x@{t.FatigueFullHours:0.0}h" : "关")}/" +
               $"战斗抖动{t.CombatJitterRatio:P0}]";
    }
}
