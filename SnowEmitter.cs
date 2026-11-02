using System;
using System.Collections.Generic;

namespace DesktopRain;

/// <summary>一片雪花。位置每帧由“基准横坐标 + 正弦摆动”算出，看起来是飘而不是横着飞。</summary>
internal struct Flake
{
    public float BaseX;    // 不含摆动的横坐标
    public float X;        // 实际绘制用
    public float Y;
    public float Vy;       // 下落速度 px/s
    public float R;        // 可见半径（像素）
    public float Alpha;    // 固有透明度，叠加全局不透明度
    public float Age;
    public float Phase;    // 摆动初相
    public float SwayAmp;  // 摆幅 px
    public float SwayFreq; // 摆动频率 Hz
}

/// <summary>
/// 雪花发射器。和雨是两套独立参数：
/// 雨靠“速度 × 拖尾系数”画线，雪靠软边贴图叠出蓬松感，所以速度只有雨的 1/10（30~80 px/s），
/// 再叠一层左右摆动和远近视差（大雪花更近、落得更快、更不透明）。
/// </summary>
internal sealed class SnowEmitter
{
    public readonly List<Flake> Items = new(1024);

    public float Amount = 1f;      // 雪量倍率
    public float SpeedScale = 1f;  // 下落速度倍率
    public float SizeScale = 1f;   // 雪花大小倍率
    public float WindX = -9f;      // 轻微向左飘

    public int Target;

    /// <summary>速度范围（1.0× 时），给日志看的。</summary>
    public float SpeedMin => 34f * SpeedScale;
    public float SpeedMax => 80f * SpeedScale;

    private readonly Random _rnd = new();

    public void Apply(float amount, float speedScale, float sizeScale)
    {
        Amount = Math.Clamp(amount, 0.1f, 3f);
        SpeedScale = Math.Clamp(speedScale, 0.2f, 3f);
        SizeScale = Math.Clamp(sizeScale, 0.4f, 3f);
    }

    /// <summary>按视口面积换算目标片数，基数 240（雪粒小而多，才铺得出漫天细雪）。</summary>
    public void ScaleTo(double viewW, double viewH)
    {
        float area = (float)Math.Sqrt(Math.Max(1.0, viewW * viewH) / 300000.0);
        int n = (int)Math.Round(240.0 * area * Amount);
        if (n < 40) n = 40;
        if (n > 1600) n = 1600;
        Target = n;
    }

    public void Clear() => Items.Clear();

    public void Spawn(double viewW, double viewH)
    {
        var f = new Flake
        {
            R = (1.1f + (float)_rnd.NextDouble() * 2.4f) * SizeScale
        };

        // 视差：半径越大越“近”，落得稍快、也更实
        float near = Math.Min(1f, f.R / Math.Max(0.001f, 3.5f * SizeScale));
        float depth = 0.72f + 0.28f * near;

        f.Vy = (34f + (float)_rnd.NextDouble() * 46f) * depth * SpeedScale;
        f.BaseX = (float)(_rnd.NextDouble() * viewW);
        f.X = f.BaseX;
        f.Y = (float)(-30.0 - _rnd.NextDouble() * 60.0);
        f.Age = 0f;
        f.Phase = (float)(_rnd.NextDouble() * Math.PI * 2.0);
        f.SwayAmp = (6f + (float)_rnd.NextDouble() * 16f) * Math.Min(1.4f, SizeScale);
        f.SwayFreq = 0.18f + (float)_rnd.NextDouble() * 0.40f;
        f.Alpha = 0.50f + (float)_rnd.NextDouble() * 0.50f;

        Items.Add(f);
    }

    public void Fill(double viewW, double viewH)
    {
        int guard = 0;
        while (Items.Count < Target && guard++ < 2000)
            Spawn(viewW, viewH);

        if (Items.Count > Target + 12)
            Items.RemoveRange(Target, Items.Count - Target);
    }

    /// <summary>首次填充：先铺满屏幕，避免雪从天上慢慢落下来。</summary>
    public void Prime(double viewW, double viewH)
    {
        Fill(viewW, viewH);
        for (int i = 0; i < Items.Count; i++)
        {
            var f = Items[i];
            f.Y = (float)(_rnd.NextDouble() * viewH);
            f.Age = (float)(_rnd.NextDouble() * 20.0);
            Items[i] = f;
        }
    }

    public void Step(float dt, double viewW, double viewH)
    {
        const float TwoPi = 6.2831853f;

        for (int i = Items.Count - 1; i >= 0; i--)
        {
            var f = Items[i];
            f.Age += dt;
            f.Y += f.Vy * dt;
            f.BaseX += WindX * dt;
            f.X = f.BaseX + (float)Math.Sin(f.Age * f.SwayFreq * TwoPi + f.Phase) * f.SwayAmp;

            bool dead = f.Y > viewH + 40.0 || f.BaseX < -140.0 || f.BaseX > viewW + 140.0;
            if (dead) Items.RemoveAt(i);
            else Items[i] = f;
        }
    }
}
