using System;
using System.Collections.Generic;

namespace DesktopRain;

internal enum RainPreset
{
    Drizzle = 0,
    Light = 1,
    Normal = 2,
    Heavy = 3,
    Storm = 4
}

/// <summary>单个雨滴。字段与 VPet 天气插件 FxParticle 一致（只保留雨用得到的）。</summary>
internal struct Drop
{
    public float X;
    public float Y;
    public float Vx;
    public float Vy;
    public float Age;
    public float Life;
    public float Drift;
}

/// <summary>
/// 雨滴粒子发射器。物理参数照抄 VPet.Plugin.Weather 的 FxPreset.Apply(Rain)：
/// 颜色 RGB(178,205,236)，速度 520~900 px/s，风 -45 px/s，拖尾长度 = 0.022 × 速度。
/// </summary>
internal sealed class RainEmitter
{
    public readonly List<Drop> Items = new(1024);

    public float SpeedMin = 520f;
    public float SpeedMax = 900f;
    public float SpeedScale = 1f;
    public float WindX = -45f;
    public float DriftScale = 12f;

    public int Target;

    private readonly Random _rnd = new();

    /// <summary>档位系数：数量倍率、速度倍率。参考原版 Light 0.55/0.8、Normal 1.0/1.0、Strong 1.7/1.25。</summary>
    public static (float count, float speed) PresetFactors(RainPreset p) => p switch
    {
        RainPreset.Drizzle => (0.35f, 0.72f),
        RainPreset.Light => (0.60f, 0.85f),
        RainPreset.Heavy => (1.45f, 1.15f),
        RainPreset.Storm => (1.90f, 1.30f),
        _ => (1.00f, 1.00f)
    };

    /// <summary>
    /// 套用档位。速度档位 × 用户速度倍率一起算，
    /// 已在飞的雨滴按同比例重算 Vy（否则要等整屏雨换一轮才看得出快慢变化）。
    /// </summary>
    public void ApplyPreset(RainPreset p, float densityScale, float speedScale = 1f)
    {
        var (cf, sf) = PresetFactors(p);

        speedScale = Math.Clamp(speedScale, 0.2f, 3f);
        float newMin = 520f * sf * speedScale;
        float newMax = 900f * sf * speedScale;

        // 用最小速度做比例基准：档位与倍率都只等比缩放，比例即速度比
        if (SpeedMin > 1f)
        {
            float ratio = newMin / SpeedMin;
            if (Math.Abs(ratio - 1f) > 0.001f)
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    var d = Items[i];
                    d.Vy *= ratio;
                    d.Life = (d.Life - d.Age) / ratio + d.Age; // 剩余飞行时间 ∝ 1/速度
                    Items[i] = d;
                }
            }
        }

        SpeedScale = speedScale;
        SpeedMin = newMin;
        SpeedMax = newMax;
        _countFactor = cf * Math.Clamp(densityScale, 0.05f, 3f);
    }

    private float _countFactor = 1f;

    /// <summary>按视口面积换算目标粒子数（原版：80 × sqrt(w*h/300000)，再乘档位系数）。</summary>
    public void ScaleTo(double viewW, double viewH)
    {
        float area = (float)Math.Sqrt(Math.Max(1.0, viewW * viewH) / 300000.0);
        int n = (int)Math.Round(80.0 * area * _countFactor);
        if (n < 6) n = 6;
        if (n > 900) n = 900;
        Target = n;
    }

    public void Clear() => Items.Clear();

    public void Spawn(double viewW, double viewH)
    {
        var d = new Drop
        {
            Drift = (float)((_rnd.NextDouble() - 0.5) * 2.0)
        };
        d.X = (float)(_rnd.NextDouble() * viewW);
        d.Y = (float)(-20.0 - _rnd.NextDouble() * 40.0);
        d.Vy = SpeedMin + (float)_rnd.NextDouble() * (SpeedMax - SpeedMin);
        d.Vx = WindX + d.Drift * DriftScale;
        d.Life = (float)((viewH + 120.0) / Math.Max(1.0, d.Vy)) + 1.0f;
        Items.Add(d);
    }

    public void Fill(double viewW, double viewH)
    {
        int guard = 0;
        while (Items.Count < Target && guard++ < 2000)
            Spawn(viewW, viewH);

        // 调小雨量时把多余的雨滴直接砍掉，不必等它们飞出屏幕
        if (Items.Count > Target + 12)
            Items.RemoveRange(Target, Items.Count - Target);
    }

    /// <summary>首次填充：直接把粒子铺满屏幕，避免雨从顶部慢慢落下来。</summary>
    public void Prime(double viewW, double viewH)
    {
        Fill(viewW, viewH);
        for (int i = 0; i < Items.Count; i++)
        {
            var d = Items[i];
            d.Y = (float)(_rnd.NextDouble() * viewH);
            d.Age = 0f;
            Items[i] = d;
        }
    }

    public void Step(float dt, double viewW, double viewH)
    {
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            var d = Items[i];
            d.Age += dt;
            d.X += d.Vx * dt;
            d.Y += d.Vy * dt;

            bool dead = d.Y > viewH + 30.0 || d.Y < -120.0 || d.X < -80.0 || d.X > viewW + 80.0 || d.Age >= d.Life;
            if (dead) Items.RemoveAt(i);
            else Items[i] = d;
        }
    }
}
