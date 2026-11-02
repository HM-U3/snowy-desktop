using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DesktopRain;

/// <summary>系统托盘。只保留「打开设置 / 暂停 / 退出」三个入口，具体选项都在 <see cref="SettingsForm"/> 窗口里。</summary>
internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly AppConfig _cfg;
    private readonly RainOverlay _overlay;
    private readonly ToolStripMenuItem _itemToggle;

    private SettingsForm _form;

    public TrayController(AppConfig cfg, RainOverlay overlay)
    {
        _cfg = cfg;
        _overlay = overlay;

        var menu = new ContextMenuStrip { ShowImageMargin = false };

        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => ShowSettings())
        {
            Font = new Font(menu.Font, FontStyle.Bold)
        });
        menu.Items.Add(new ToolStripSeparator());

        _itemToggle = new ToolStripMenuItem("暂停特效", null, (_, _) => Enabled = !Enabled);
        menu.Items.Add(_itemToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => Application.Exit()));

        _icon = new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "桌面雨雪 · 单击打开设置",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowSettings();
        };
        _icon.DoubleClick += (_, _) => ShowSettings();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            _overlay.SetPaused(!value);
            _itemToggle.Text = value ? "暂停特效" : "恢复特效";
            _icon.Text = value ? "桌面雨雪 · 单击打开设置" : "桌面雨雪（已暂停）";
            if (_form is { IsDisposed: false, Visible: true }) _form.RefreshFromConfig();
        }
    }
    private bool _enabled = true;

    /// <summary>打开（或前置）设置窗口。托盘点击、二次启动、托盘菜单都走这里。</summary>
    public void ShowSettings()
    {
        if (_form == null || _form.IsDisposed)
            _form = new SettingsForm(_cfg, _overlay, this);

        _form.RefreshFromConfig();
        if (!_form.Visible) _form.Show();
        if (_form.WindowState == FormWindowState.Minimized)
            _form.WindowState = FormWindowState.Normal;

        // 从后台被叫出来时（二次启动 / 定时器触发），系统不允许直接抢焦点，
        // 短暂切一次 TopMost 是最可靠的强制前置手法。
        if (!_form.TopMost)
        {
            _form.TopMost = true;
            _form.TopMost = false;
        }
        _form.Activate();
        _form.BringToFront();
    }

    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var pen = new Pen(Color.FromArgb(235, 178, 205, 236), 3.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLine(pen, 10, 5, 6, 15);
            g.DrawLine(pen, 19, 9, 15, 20);
            g.DrawLine(pen, 27, 14, 23, 25);
        }
        IntPtr h = bmp.GetHicon();
        return (Icon)Icon.FromHandle(h).Clone();
    }

    public void Dispose()
    {
        if (_form is { IsDisposed: false }) _form.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>开机自启（HKCU Run 项）。</summary>
internal static class Autostart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopRain";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    public static void Set(bool on)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath, true);
            if (key == null) return;
            if (on)
            {
                string exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue(ValueName, "\"" + exe + "\"");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception ex)
        {
            AppConfig.Log("设置开机自启失败: " + ex.Message);
        }
    }
}
