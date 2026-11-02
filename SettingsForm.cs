using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace DesktopRain;

/// <summary>
/// 独立的设置窗口：顶部「雨 / 雪」两个页面各自是一套独立参数，底下是两边共用的设置。
/// 所有改动即时生效并写入配置，不需要「确定」。
/// 布局按 DPI（DeviceDpi/96）等比缩放，窗口高度由内容撑开，避免高 DPI 下互相压字。
/// </summary>
internal sealed class SettingsForm : Form
{
    private static readonly Color Bg = Color.FromArgb(247, 248, 250);
    private static readonly Color Accent = Color.FromArgb(46, 86, 132);
    private static readonly Color AccentHover = Color.FromArgb(62, 108, 158);
    private static readonly Color Greenish = Color.FromArgb(88, 148, 108);
    private static readonly Color GreenishHover = Color.FromArgb(104, 168, 124);
    private static readonly Color TextMain = Color.FromArgb(32, 34, 38);
    private static readonly Color TextSub = Color.FromArgb(118, 124, 132);
    private static readonly Color BorderLine = Color.FromArgb(210, 215, 222);

    private const int PadX = 24;
    private const int ContentW = 412;
    private const int WinW = 460;

    // 页面内的行位置（逻辑像素），两页严格共用同一套，切换时窗口不跳动
    private const int RowT1 = 0, RowC1 = 24;    // 区块标题 / 第一行控件
    private const int RowT2 = 70, RowC2 = 94;
    private const int RowT3 = 141, RowC3 = 165;
    private const int RowT4 = 212, RowC4 = 236;
    private const int PageH = 278;

    private readonly AppConfig _cfg;
    private readonly RainOverlay _overlay;
    private readonly TrayController _host;

    private SegmentButton _tabRain;
    private SegmentButton _tabSnow;
    private Panel _pageRain;
    private Panel _pageSnow;

    // 雨页
    private readonly SegmentButton[] _presets = new SegmentButton[5];
    private TrackBar _rSpeed;
    private Label _rSpeedText;
    private TrackBar _rOpacity;
    private Label _rOpacityText;
    private TrackBar _rTail;
    private Label _rTailText;

    // 雪页
    private readonly SegmentButton[] _snowAmounts = new SegmentButton[5];
    private TrackBar _sSpeed;
    private Label _sSpeedText;
    private TrackBar _sSize;
    private Label _sSizeText;
    private TrackBar _sOpacity;
    private Label _sOpacityText;

    // 公共
    private readonly SegmentButton[] _fps = new SegmentButton[3];
    private SegmentButton _segTop;
    private SegmentButton _segBottom;
    private CheckBox _chkFullscreen;
    private CheckBox _chkTaskbar;
    private CheckBox _chkAutostart;
    private Button _btnPause;

    private float _k = 1f;
    private int _py;
    private bool _sync;

    private static readonly RainPreset[] PresetValues =
    {
        RainPreset.Drizzle, RainPreset.Light, RainPreset.Normal, RainPreset.Heavy, RainPreset.Storm
    };

    private static readonly string[] PresetNames = { "毛毛雨", "小雨", "中雨", "大雨", "暴雨" };

    private static readonly float[] SnowLevels = { 0.4f, 0.65f, 1.0f, 1.4f, 1.8f };
    private static readonly string[] SnowLevelNames = { "微雪", "小雪", "中雪", "大雪", "暴雪" };

    private static Font F(float pt, FontStyle style = FontStyle.Regular)
        => new Font("Microsoft YaHei UI", pt, style);

    private int S(int v) => (int)Math.Round(v * _k);
    private int L(int px) => (int)Math.Round(px / _k);   // 像素 → 逻辑
    private Size Z(int w, int h) => new Size(S(w), S(h));

    private static string MulText(int percent)
        => (percent / 100f).ToString("0.0", CultureInfo.InvariantCulture) + "×";

    public SettingsForm(AppConfig cfg, RainOverlay overlay, TrayController host)
    {
        _cfg = cfg;
        _overlay = overlay;
        _host = host;

        using (var g = Graphics.FromHwnd(IntPtr.Zero))
        {
            _k = Math.Max(1f, g.DpiY / 96f);
        }

        SuspendLayout();

        Text = "桌面雨雪 · 设置";
        Font = F(9f);
        BackColor = Bg;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        TopMost = !cfg.BottomLayer;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _py = 18;   // 之后 _py 一律是逻辑像素
        BuildHeader();
        BuildTabs();
        BuildPages();
        BuildFps();
        BuildLayer();
        BuildOptions();

        int contentBottom = _py;
        ClientSize = new Size(S(WinW), S(contentBottom + 78));
        BuildFooter(contentBottom);

        ResumeLayout(false);
        PerformLayout();

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                _cfg.Save();
                Hide();
            }
        };

        RefreshFromConfig();
    }

    // ---------------- 头部与页签 ----------------

    private void BuildHeader()
    {
        var title = new Label
        {
            Text = "桌面雨雪",
            Font = F(15f, FontStyle.Bold),
            ForeColor = TextMain,
            AutoSize = true,
            Location = new Point(S(PadX), _py)
        };
        Controls.Add(title);
        _py = L(title.Bottom) + 1;

        var sub = new Label
        {
            Text = "改动立即生效，关掉这个窗口就行",
            Font = F(8.5f),
            ForeColor = TextSub,
            AutoSize = true,
            Location = new Point(S(PadX), S(_py))
        };
        Controls.Add(sub);
        _py = L(sub.Bottom) + 12;
    }

    private void BuildTabs()
    {
        int w = (ContentW - 6) / 2;

        _tabRain = new SegmentButton
        {
            Text = "雨",
            Size = Z(w, 36),
            Location = new Point(S(PadX), S(_py)),
            Font = F(10.5f, FontStyle.Bold)
        };
        _tabSnow = new SegmentButton
        {
            Text = "雪",
            Size = Z(w, 36),
            Location = new Point(S(PadX + w + 6), S(_py)),
            Font = F(10.5f, FontStyle.Bold)
        };
        _tabRain.Click += (_, _) => SetMode("rain");
        _tabSnow.Click += (_, _) => SetMode("snow");

        Controls.Add(_tabRain);
        Controls.Add(_tabSnow);
        _py += 36 + 14;
    }

    private void SetMode(string mode)
    {
        _cfg.Mode = mode;
        Apply();
        RefreshFromConfig();
    }

    private void BuildPages()
    {
        int top = _py;

        _pageRain = NewPage(top);
        _pageSnow = NewPage(top);
        Controls.Add(_pageRain);
        Controls.Add(_pageSnow);

        BuildRainPage(_pageRain);
        BuildSnowPage(_pageSnow);

        _py = top + PageH + 10;
    }

    private Panel NewPage(int top) => new Panel
    {
        Location = new Point(0, S(top)),
        Size = new Size(S(WinW), S(PageH)),
        BackColor = Bg,
        Visible = false
    };

    // ---------------- 雨页 ----------------

    private void BuildRainPage(Panel p)
    {
        AddSection(p, "雨量（五档）", RowT1);
        BuildSegmentRow(p, RowC1, PresetNames, PresetValues.Length, i =>
        {
            _cfg.Preset = PresetValues[i].ToString();
            Apply();
            RefreshFromConfig();
        }, _presets);

        AddSection(p, "下落速度（0.3×–2.2×）", RowT2);
        _rSpeed = AddSlider(p, RowC2, 30, 220, 5, 20, out _rSpeedText);
        _rSpeed.ValueChanged += (_, _) =>
        {
            _rSpeedText.Text = MulText(_rSpeed.Value);
            if (_sync) return;
            _cfg.SpeedScale = _rSpeed.Value / 100f;
            Apply(false);
        };
        _rSpeed.MouseUp += (_, _) => _cfg.Save();
        _rSpeed.KeyUp += (_, _) => _cfg.Save();

        AddSection(p, "不透明度（10%–100%）", RowT3);
        _rOpacity = AddSlider(p, RowC3, 10, 100, 1, 10, out _rOpacityText);
        _rOpacity.ValueChanged += (_, _) =>
        {
            _rOpacityText.Text = _rOpacity.Value + "%";
            if (_sync) return;
            _cfg.Opacity = _rOpacity.Value / 100f;
            Apply(false);
        };
        _rOpacity.MouseUp += (_, _) => _cfg.Save();
        _rOpacity.KeyUp += (_, _) => _cfg.Save();

        AddSection(p, "雨丝长度（0.020–0.080）", RowT4);
        _rTail = AddSlider(p, RowC4, 20, 80, 2, 10, out _rTailText);
        _rTail.ValueChanged += (_, _) =>
        {
            _rTailText.Text = "0." + _rTail.Value.ToString("000", CultureInfo.InvariantCulture);
            if (_sync) return;
            _cfg.TailFactor = _rTail.Value / 1000f;
            Apply(false);
        };
        _rTail.MouseUp += (_, _) => _cfg.Save();
        _rTail.KeyUp += (_, _) => _cfg.Save();
    }

    // ---------------- 雪页 ----------------

    private void BuildSnowPage(Panel p)
    {
        AddSection(p, "雪量（五档）", RowT1);
        BuildSegmentRow(p, RowC1, SnowLevelNames, SnowLevels.Length, i =>
        {
            _cfg.SnowAmount = SnowLevels[i];
            Apply();
            RefreshFromConfig();
        }, _snowAmounts);

        AddSection(p, "下落速度（0.3×–2.2×）", RowT2);
        _sSpeed = AddSlider(p, RowC2, 30, 220, 5, 20, out _sSpeedText);
        _sSpeed.ValueChanged += (_, _) =>
        {
            _sSpeedText.Text = MulText(_sSpeed.Value);
            if (_sync) return;
            _cfg.SnowSpeed = _sSpeed.Value / 100f;
            Apply(false);
        };
        _sSpeed.MouseUp += (_, _) => _cfg.Save();
        _sSpeed.KeyUp += (_, _) => _cfg.Save();

        AddSection(p, "雪花大小（0.6×–2.0×）", RowT3);
        _sSize = AddSlider(p, RowC3, 60, 200, 5, 20, out _sSizeText);
        _sSize.ValueChanged += (_, _) =>
        {
            _sSizeText.Text = MulText(_sSize.Value);
            if (_sync) return;
            _cfg.SnowSize = _sSize.Value / 100f;
            Apply(false);
        };
        _sSize.MouseUp += (_, _) => _cfg.Save();
        _sSize.KeyUp += (_, _) => _cfg.Save();

        AddSection(p, "不透明度（30%–100%）", RowT4);
        _sOpacity = AddSlider(p, RowC4, 30, 100, 1, 10, out _sOpacityText);
        _sOpacity.ValueChanged += (_, _) =>
        {
            _sOpacityText.Text = _sOpacity.Value + "%";
            if (_sync) return;
            _cfg.SnowOpacity = _sOpacity.Value / 100f;
            Apply(false);
        };
        _sOpacity.MouseUp += (_, _) => _cfg.Save();
        _sOpacity.KeyUp += (_, _) => _cfg.Save();
    }

    // ---------------- 公共部分 ----------------

    private void BuildFps()
    {
        AddSection(this, "帧率", _py);
        _py += S(19) + S(5);

        int[] values = { 24, 30, 60 };
        int w = 76;
        for (int i = 0; i < values.Length; i++)
        {
            int v = values[i];
            var b = new SegmentButton
            {
                Text = v + " fps",
                Size = Z(w, 32),
                Location = new Point(S(PadX) + i * S(w + 6), S(_py))
            };
            b.Click += (_, _) =>
            {
                _cfg.TargetFps = v;
                Apply();
                RefreshFromConfig();
            };
            _fps[i] = b;
            Controls.Add(b);
        }
        _py += 32 + 14;
    }

    private void BuildLayer()
    {
        AddSection(this, "显示层级", _py);
        _py += 19 + 5;

        int w = (ContentW - 6) / 2;
        _segTop = new SegmentButton
        {
            Text = "总在最上层",
            Size = Z(w, 32),
            Location = new Point(S(PadX), S(_py))
        };
        _segBottom = new SegmentButton
        {
            Text = "压在窗口下层",
            Size = Z(w, 32),
            Location = new Point(S(PadX + w + 6), S(_py))
        };
        _segTop.Click += (_, _) => SetLayer("top");
        _segBottom.Click += (_, _) => SetLayer("bottom");
        Controls.Add(_segTop);
        Controls.Add(_segBottom);
        _py += 32 + 6;

        var hint = new Label
        {
            Text = "最上层氛围最强，鼠标仍可穿透；压在窗口下层则只出现在壁纸上，"
                 + "任何窗口都能盖住它，绝不可能挡住点击。",
            Font = F(8f),
            ForeColor = TextSub,
            AutoSize = false,
            Size = Z(ContentW, 34),
            Location = new Point(S(PadX), S(_py))
        };
        Controls.Add(hint);
        _py += 34 + 12;
    }

    private void BuildOptions()
    {
        AddSection(this, "选项", _py);
        _py += 19 + 5;

        int w = (ContentW - 8) / 2;
        int y = _py;

        _chkTaskbar = MakeCheck("覆盖任务栏", PadX, y, w);
        _chkTaskbar.CheckedChanged += (_, _) =>
        {
            if (_sync) return;
            _cfg.CoverTaskbar = _chkTaskbar.Checked;
            _overlay.Relayout(_cfg.CoverTaskbar);
            _cfg.Save();
        };

        _chkFullscreen = MakeCheck("全屏时自动隐藏", PadX + w + 8, y, w);
        _chkFullscreen.CheckedChanged += (_, _) =>
        {
            if (_sync) return;
            _cfg.HideOnFullscreen = _chkFullscreen.Checked;
            Apply();
        };

        y += 28;

        _chkAutostart = MakeCheck("开机自启", PadX, y, w);
        _chkAutostart.CheckedChanged += (_, _) =>
        {
            if (_sync) return;
            Autostart.Set(_chkAutostart.Checked);
        };

        _py = y + 28 + 4;
    }

    private void BuildFooter(int contentBottom)
    {
        var line = new Panel
        {
            BackColor = BorderLine,
            Location = new Point(S(PadX), S(contentBottom + 6)),
            Size = new Size(S(ContentW), Math.Max(1, S(1)))
        };
        Controls.Add(line);

        int y = ClientSize.Height - S(22) - S(38);

        _btnPause = new Button
        {
            Text = "暂停特效",
            Size = Z(126, 38),
            Location = new Point(S(PadX), y),
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
            Font = F(9.5f, FontStyle.Bold)
        };
        _btnPause.FlatAppearance.BorderSize = 0;
        _btnPause.FlatAppearance.MouseOverBackColor = AccentHover;
        _btnPause.Click += (_, _) =>
        {
            _host.Enabled = !_host.Enabled;
            RefreshFromConfig();
        };
        Controls.Add(_btnPause);

        var close = new Button
        {
            Text = "关闭",
            Size = Z(96, 38),
            Location = new Point(S(WinW - PadX) - S(96), y),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = TextMain,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
            Font = F(9.5f)
        };
        close.FlatAppearance.BorderColor = BorderLine;
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(238, 241, 245);
        close.Click += (_, _) => Hide();
        Controls.Add(close);
    }

    // ---------------- 控件工厂 ----------------

    private void AddSection(Control host, string title, int y)
    {
        var l = new Label
        {
            Text = title,
            Font = F(9f, FontStyle.Bold),
            ForeColor = TextSub,
            AutoSize = true,
            Location = new Point(S(PadX), S(y))
        };
        host.Controls.Add(l);
    }

    private void BuildSegmentRow(Control host, int y, string[] labels, int count,
                                 Action<int> onClick, SegmentButton[] into)
    {
        const int gap = 6;
        int w = (ContentW - gap * (count - 1)) / count;

        for (int i = 0; i < count; i++)
        {
            int idx = i;
            var b = new SegmentButton
            {
                Text = labels[i],
                Size = Z(w, 32),
                Location = new Point(S(PadX) + i * S(w + gap), S(y))
            };
            b.Click += (_, _) => onClick(idx);
            into[i] = b;
            host.Controls.Add(b);
        }
    }

    private TrackBar AddSlider(Control host, int y, int min, int max, int small, int large,
                               out Label valueLabel)
    {
        var t = new TrackBar
        {
            Minimum = min,
            Maximum = max,
            TickStyle = TickStyle.None,
            SmallChange = small,
            LargeChange = large,
            AutoSize = false,
            Size = new Size(S(ContentW) - S(70), S(32)),
            Location = new Point(S(PadX - 6), S(y)),
            BackColor = Bg
        };

        valueLabel = new Label
        {
            Text = "",
            Font = F(10f, FontStyle.Bold),
            ForeColor = TextMain,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Size = Z(56, 30),
            Location = new Point(S(WinW - PadX) - S(56), S(y))
        };

        host.Controls.Add(t);
        host.Controls.Add(valueLabel);
        return t;
    }

    private CheckBox MakeCheck(string text, int x, int y, int w)
    {
        var c = new CheckBox
        {
            Text = text,
            Font = F(9f),
            ForeColor = TextMain,
            AutoSize = false,
            Size = Z(w, 24),
            Location = new Point(S(x - 2), S(y)),
            BackColor = Bg
        };
        Controls.Add(c);
        return c;
    }

    // ---------------- 状态同步 ----------------

    private void SetLayer(string layer)
    {
        _cfg.Layer = layer;
        _overlay.SetLayer(_cfg.BottomLayer);
        _cfg.Save();
        TopMost = !_cfg.BottomLayer;
        RefreshFromConfig();
    }

    private void Apply(bool save = true)
    {
        if (save) _cfg.Save();
        _overlay.ApplyConfig(_cfg);
    }

    /// <summary>把配置回灌到控件（切页、托盘改动、二次启动后都走这里）。</summary>
    public void RefreshFromConfig()
    {
        _sync = true;
        try
        {
            bool snow = _cfg.SnowMode;

            _tabRain.Checked = !snow;
            _tabSnow.Checked = snow;
            _pageRain.Visible = !snow;
            _pageSnow.Visible = snow;

            for (int i = 0; i < _presets.Length; i++)
                _presets[i].Checked = _cfg.PresetValue == PresetValues[i];

            _rSpeed.Value = Percent(_cfg.SpeedScale, 30, 220);
            _rSpeedText.Text = MulText(_rSpeed.Value);

            _rOpacity.Value = (int)Math.Round(Math.Clamp(_cfg.Opacity, 0.10f, 1f) * 100);
            _rOpacityText.Text = _rOpacity.Value + "%";

            _rTail.Value = (int)Math.Round(Math.Clamp(_cfg.TailFactor, 0.020f, 0.080f) * 1000f);
            _rTailText.Text = "0." + _rTail.Value.ToString("000", CultureInfo.InvariantCulture);

            float best = float.MaxValue;
            int pick = 2;
            for (int i = 0; i < SnowLevels.Length; i++)
            {
                float d = Math.Abs(_cfg.SnowAmount - SnowLevels[i]);
                if (d < best) { best = d; pick = i; }
            }
            for (int i = 0; i < _snowAmounts.Length; i++)
                _snowAmounts[i].Checked = i == pick;

            _sSpeed.Value = Percent(_cfg.SnowSpeed, 30, 220);
            _sSpeedText.Text = MulText(_sSpeed.Value);

            _sSize.Value = Percent(_cfg.SnowSize, 60, 200);
            _sSizeText.Text = MulText(_sSize.Value);

            _sOpacity.Value = (int)Math.Round(Math.Clamp(_cfg.SnowOpacity, 0.30f, 1f) * 100);
            _sOpacityText.Text = _sOpacity.Value + "%";

            _fps[0].Checked = _cfg.TargetFps == 24;
            _fps[1].Checked = _cfg.TargetFps == 30;
            _fps[2].Checked = _cfg.TargetFps == 60;

            bool bottom = _cfg.BottomLayer;
            _segBottom.Checked = bottom;
            _segTop.Checked = !bottom;

            _chkFullscreen.Checked = _cfg.HideOnFullscreen;
            _chkTaskbar.Checked = _cfg.CoverTaskbar;
            _chkAutostart.Checked = Autostart.IsEnabled();

            _btnPause.Text = _host.Enabled ? "暂停特效" : "恢复特效";
            _btnPause.BackColor = _host.Enabled ? Accent : Greenish;
            _btnPause.FlatAppearance.MouseOverBackColor = _host.Enabled ? AccentHover : GreenishHover;
        }
        finally
        {
            _sync = false;
        }
    }

    /// <summary>把 0.xx 的倍率换算成滑杆刻度，并夹到控件允许的范围内。</summary>
    private static int Percent(float ratio, int min, int max)
        => Math.Clamp((int)Math.Round(ratio * 100f), min, max);

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        RefreshFromConfig();
    }

    // ---------------- 分段按钮 ----------------

    private sealed class SegmentButton : Button
    {
        private bool _checked;

        public SegmentButton()
        {
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            Font = F(9f);
            FlatAppearance.BorderSize = 1;
            SetStyle(ControlStyles.ResizeRedraw, true);
            ApplyVisual();
        }

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                ApplyVisual();
                Invalidate();
            }
        }

        private void ApplyVisual()
        {
            if (_checked)
            {
                BackColor = Accent;
                ForeColor = Color.White;
                FlatAppearance.BorderColor = Accent;
                FlatAppearance.MouseOverBackColor = AccentHover;
            }
            else
            {
                BackColor = Color.White;
                ForeColor = TextMain;
                FlatAppearance.BorderColor = BorderLine;
                FlatAppearance.MouseOverBackColor = Color.FromArgb(238, 241, 245);
            }
        }
    }
}
