using System;
using System.Threading;
using System.Windows.Forms;

namespace DesktopRain;

internal static class Program
{
    /// <summary>二次启动时用它通知已在运行的实例打开设置窗口。</summary>
    private const string OpenSettingsEvent = @"Global\DesktopRain.OpenSettings";

    private static int _openRequested;

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Global\DesktopRain.SingleInstance", out bool isNew);
        using var openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSettingsEvent);

        if (!isNew)
        {
            // 已经在运行：不再弹框，直接把设置窗口叫出来
            try { openEvent.Set(); } catch { }
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();

        AppConfig cfg = AppConfig.Load();

        var overlay = new RainOverlay();
        try
        {
            overlay.SetLayer(cfg.BottomLayer);
            overlay.Create();
        }
        catch (Exception ex)
        {
            AppConfig.Log("初始化失败: " + ex);
            MessageBox.Show("桌面雨效启动失败：\n\n" + ex.Message, "DesktopRain",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        overlay.ApplyConfig(cfg);
        overlay.Relayout(cfg.CoverTaskbar);
        overlay.Start();

        // --paused 启动即为「已关闭雨效」状态（此时窗口整体隐藏，不占任何命中测试）
        bool startPaused = false;
        bool startSettings = false;
        foreach (string a in args)
        {
            if (string.Equals(a, "--paused", StringComparison.OrdinalIgnoreCase)) startPaused = true;
            if (string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)) startSettings = true;
        }
        if (startPaused) overlay.SetPaused(true);

        var tray = new TrayController(cfg, overlay);
        if (startPaused) tray.Enabled = false; // 让托盘菜单的勾选状态与实际一致
        if (startSettings) tray.ShowSettings();

        // 监听「二次启动」请求：后台线程等事件，UI 线程的定时器负责真正打开窗口
        var watcher = new Thread(() =>
        {
            while (true)
            {
                if (!openEvent.WaitOne()) continue;
                Interlocked.Exchange(ref _openRequested, 1);
            }
        })
        { IsBackground = true, Name = "SettingsWatcher" };
        watcher.Start();

        int tick = 0;
        int fullscreenHits = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 800 };
        timer.Tick += (_, _) =>
        {
            try
            {
                // 二次启动（比如又双击了桌面图标）→ 打开设置窗口
                if (Interlocked.Exchange(ref _openRequested, 0) == 1) tray.ShowSettings();

                // 连续三次判定全屏才隐藏（约 2.4s），避免切换窗口时的浮层误判；恢复立即生效
                if (cfg.HideOnFullscreen && Native.IsForegroundFullscreen(overlay.Handle))
                    fullscreenHits++;
                else
                    fullscreenHits = 0;

                overlay.SetHidden(fullscreenHits >= 3);

                // 每 ~4 秒检查一次分辨率/任务栏变化
                if (++tick >= 5)
                {
                    tick = 0;
                    Native.RECT want = Native.GetPrimaryWorkArea();
                    Native.RECT cur = overlay.CurrentRect;
                    if (want.Left != cur.Left || want.Top != cur.Top ||
                        want.Right != cur.Right || want.Bottom != cur.Bottom)
                    {
                        overlay.Relayout(cfg.CoverTaskbar);
                    }
                }
            }
            catch (Exception ex)
            {
                AppConfig.Log("定时任务异常: " + ex.Message);
            }
        };
        timer.Start();

        Application.ApplicationExit += (_, _) =>
        {
            timer.Stop();
            tray.Dispose();
            overlay.Dispose();
            AppConfig.Log("退出");
        };

        AppConfig.Log($"启动: 特效={(cfg.SnowMode ? "雪" : "雨")} 档位={cfg.Preset} "
                    + $"不透明度={cfg.Opacity:0.00} 帧率={cfg.TargetFps}");
        Application.Run(new ApplicationContext());
    }
}
