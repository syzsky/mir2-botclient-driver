using System;
using System.Collections.Generic;
using BotClient.Human;

namespace BotClient.ClientDriver.Human;

/// <summary>
/// 人化鼠标轨迹生成器。
///
/// 改造前：<c>MoveAbsolute(target)</c> 一次瞬移到位 —— 这是**最明显的机器特征**：
///   真人移动鼠标在系统里表现为一串连续的 WM_MOUSEMOVE，而瞬移只有一条"从 A 跳到 B"的记录。
/// 改造后：按三次贝塞尔生成中间点，逐点送 SendInput，且：
///   • 速度曲线用 smoothstep（起步慢 → 中段快 → 接近目标减速，真人手部就是这种加减速）；
///   • 路径带随机垂向弯曲（手不会走绝对直线）；
///   • 概率性过冲一点再回修（真人瞄点常见）。
///
/// 注意：轨迹只影响**鼠标怎么到**，不改变"点到哪" —— 终点仍是 ScreenMapper 算出的目标点
/// （只允许 ±JitterPx 的自然偏差），所以不会点偏到别的格子上。
/// </summary>
public static class HumanMousePath
{
    /// <summary>一个轨迹采样点：屏幕坐标 + 到下一段的停留毫秒。</summary>
    public readonly record struct Step(int X, int Y, int DelayMs);

    /// <summary>
    /// 生成从 (fromX,fromY) 到 (toX,toY) 的人化轨迹。
    /// 距离过近（&lt;6px）时退化为单点，避免"原地抖动"这种反而更假的动作。
    /// </summary>
    public static List<Step> Build(int fromX, int fromY, int toX, int toY, HumanTuning t)
    {
        var steps = new List<Step>();
        if (t is null || !t.Enabled)
        {
            steps.Add(new Step(toX, toY, 0));
            return steps;
        }

        double dx = toX - fromX;
        double dy = toY - fromY;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 6)
        {
            steps.Add(new Step(toX, toY, HumanTiming.Next(4, 12)));
            return steps;
        }

        int count = Math.Clamp(
            HumanTiming.Next(t.TrajectoryStepsMin, Math.Max(t.TrajectoryStepsMin + 1, t.TrajectoryStepsMax + 1)),
            3, 60);

        // 垂直单位向量 + 随机弓形偏移 → 路径不是直线
        double nx = -dy / dist;
        double ny = dx / dist;
        double bow = (HumanTiming.NextDouble() * 2 - 1) * dist * t.CurveBias;
        double c1x = fromX + dx * 0.32 + nx * bow;
        double c1y = fromY + dy * 0.32 + ny * bow;
        double c2x = fromX + dx * 0.68 + nx * bow * 0.55;
        double c2y = fromY + dy * 0.68 + ny * bow * 0.55;

        for (int i = 1; i <= count; i++)
        {
            double u = (double)i / count;
            double e = u * u * (3 - 2 * u);              // smoothstep：慢起慢停
            double x = Cubic(fromX, c1x, c2x, toX, e);
            double y = Cubic(fromY, c1y, c2y, toY, e);

            int delay = HumanTiming.Next(t.StepDelayMinMs, Math.Max(t.StepDelayMinMs + 1, t.StepDelayMaxMs + 1));
            if (u > 0.72) delay += HumanTiming.Next(2, 9);   // 接近目标时减速（真人会先慢下来再点）

            steps.Add(new Step((int)Math.Round(x), (int)Math.Round(y), delay));
        }

        // 过冲：先滑过目标一点，停一小下，再回修到目标点
        if (HumanTiming.Chance(t.OvershootChance) && dist > 40)
        {
            int over = Math.Max(4, t.OvershootPx);
            int ox = toX + (int)Math.Round(dx / dist * over);
            int oy = toY + (int)Math.Round(dy / dist * over);
            steps.Add(new Step(ox, oy, HumanTiming.Next(18, 55)));
            steps.Add(new Step(toX, toY, HumanTiming.Next(12, 40)));
        }

        return steps;
    }

    private static double Cubic(double p0, double p1, double p2, double p3, double t)
    {
        double mt = 1 - t;
        return mt * mt * mt * p0
             + 3 * mt * mt * t * p1
             + 3 * mt * t * t * p2
             + t * t * t * p3;
    }
}
