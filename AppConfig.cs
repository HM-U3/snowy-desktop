using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopRain;

/// <summary>用户配置，落在 %APPDATA%\DesktopRain\config.json。</summary>
internal sealed class AppConfig
{
    /// <summary>当前特效："rain" 雨 / "snow" 雪。两套参数各自独立保存。</summary>
    public string Mode { get; set; } = "rain";

    public string Preset { get; set; } = nameof(RainPreset.Normal);

    /// <summary>整体不透明度 0.05~1。</summary>
    public float Opacity { get; set; } = 0.68f;

    /// <summary>目标帧率。</summary>
    public int TargetFps { get; set; } = 30;

    /// <summary>在档位基础上再乘一层密度（原版按 520×640 小窗设计，铺满桌面需要更密）。</summary>
    public float DensityScale { get; set; } = 1.9f;

    /// <summary>下落速度倍率（0.3~2.2），1.0 = 原版 520~900 px/s。</summary>
    public float SpeedScale { get; set; } = 1.0f;

    /// <summary>水平风速 px/s，原版 -45。</summary>
    public float Wind { get; set; } = -45f;

    /// <summary>雨丝长度系数，原版 0.022；全屏下加长到 0.042 才像雨条。</summary>
    public float TailFactor { get; set; } = 0.042f;

    /// <summary>雨丝线宽（物理像素），原版 1.1。</summary>
    public float StrokeWidth { get; set; } = 1.3f;

    public int ColorR { get; set; } = 178;
    public int ColorG { get; set; } = 205;
    public int ColorB { get; set; } = 236;

    // ---- 雪（与雨完全独立的一套参数） ----

    /// <summary>雪量倍率，5 档按钮映射到 0.4 / 0.65 / 1.0 / 1.4 / 1.8。</summary>
    public float SnowAmount { get; set; } = 1.0f;

    /// <summary>雪的下落速度倍率 0.3~2.2（基准 34~80 px/s，本来就很慢）。</summary>
    public float SnowSpeed { get; set; } = 1.0f;

    /// <summary>雪花大小倍率 0.6~2.0。</summary>
    public float SnowSize { get; set; } = 1.0f;

    /// <summary>雪的不透明度 0.3~1。</summary>
    public float SnowOpacity { get; set; } = 0.9f;

    /// <summary>检测到前台全屏程序时自动隐身。</summary>
    public bool HideOnFullscreen { get; set; } = true;

    /// <summary>覆盖任务栏区域（默认只铺工作区）。</summary>
    public bool CoverTaskbar { get; set; } = false;

    /// <summary>
    /// 显示层级："bottom" = 桌面底层，雨在普通窗口下面，绝不参与任何点击/命中测试；
    /// "top" = 总在最上层，雨盖在所有窗口之上。
    /// </summary>
    public string Layer { get; set; } = "bottom";

    [JsonIgnore]
    public bool BottomLayer =>
        !string.Equals(Layer, "top", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool SnowMode =>
        string.Equals(Mode, "snow", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public RainPreset PresetValue =>
        Enum.TryParse<RainPreset>(Preset, true, out var p) ? p : RainPreset.Normal;

    // ---- 持久化 ----

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopRain");

    public static string ConfigPath => Path.Combine(Dir, "config.json");

    public static string LogPath => Path.Combine(Dir, "rain.log");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts);
                if (cfg != null) return cfg;
            }
        }
        catch (Exception ex)
        {
            Log("读取配置失败: " + ex.Message);
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Log("保存配置失败: " + ex.Message);
        }
    }

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
