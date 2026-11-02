using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Mathematics;

namespace DesktopRain;

/// <summary>
/// 桌面雨效窗口本体：一个全屏透明、鼠标穿透、永不被激活的置顶窗口，
/// 内容由 DirectComposition 承载的 GPU 交换链直接合成。
/// </summary>
internal sealed class RainOverlay : IDisposable
{
    private const string ClassName = "DesktopRainOverlayWnd";
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int SW_SHOWNOACTIVATE = 4;

    private static Native.WndProcDelegate _procKeepAlive; // 防止委托被 GC

    private IntPtr _hwnd;
    private GpuRainRenderer _renderer;
    private Thread _thread;
    private Native.RECT _rect;

    private volatile bool _stop;
    private volatile bool _hidden;
    private volatile bool _paused;
    private volatile bool _bottom = true; // 默认压在桌面底层，绝不挡任何点击
    private volatile bool _needResize;
    private volatile bool _needApply;
    private volatile State _state = new();

    private readonly RainEmitter _emitter = new();
    private readonly SnowEmitter _snow = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _activeSnow;   // 渲染线程当前在跑哪套粒子

    private string _lastParamKey;

    /// <summary>参数 → 两套发射器（都算一遍，切换时不用等下一帧）。</summary>
    private void ApplyEmitters(State s)
    {
        int w = _renderer.PixelWidth;
        int h = _renderer.PixelHeight;

        _emitter.ApplyPreset(s.Preset, s.Density, s.Speed);
        _emitter.ScaleTo(w, h);

        _snow.Apply(s.SnowAmount, s.SnowSpeed, s.SnowSize);
        _snow.ScaleTo(w, h);
    }

    private void PrimeActive(bool snow)
    {
        int w = _renderer.PixelWidth;
        int h = _renderer.PixelHeight;
        if (snow) _snow.Prime(w, h);
        else _emitter.Prime(w, h);
    }

    /// <summary>参数变化时记一行日志（拖动滑杆时同一个值只记一次）。</summary>
    private void LogParams(State s)
    {
        bool snow = s.Mode == "snow";
        string key = snow
            ? $"snow|{s.SnowAmount:0.00}|{s.SnowSpeed:0.00}|{s.SnowSize:0.00}|{s.SnowOpacity:0.00}"
            : $"rain|{s.Preset}|{s.Density:0.00}|{s.Speed:0.00}";
        if (key == _lastParamKey) return;
        _lastParamKey = key;

        if (snow)
            AppConfig.Log($"参数: 雪 雪量 {s.SnowAmount:0.00}, 速度 {s.SnowSpeed:0.00}× "
                        + $"({_snow.SpeedMin:0}-{_snow.SpeedMax:0} px/s), 大小 {s.SnowSize:0.00}×, "
                        + $"不透明度 {s.SnowOpacity:0.00}, 目标片数 {_snow.Target}");
        else
            AppConfig.Log($"参数: 雨 档位 {s.Preset}, 密度 {s.Density:0.00}, 速度 {s.Speed:0.00}× "
                        + $"({_emitter.SpeedMin:0}-{_emitter.SpeedMax:0} px/s), 目标粒子 {_emitter.Target}");
    }

    /// <summary>渲染线程读取的不可变状态快照。</summary>
    private sealed class State
    {
        public bool Enabled = true;
        public string Mode = "rain";
        public RainPreset Preset = RainPreset.Normal;
        public float Opacity = 0.75f;
        public float Density = 1f;
        public float Speed = 1f;
        public float Wind = -45f;
        public float Tail = 0.022f;
        public float Stroke = 1.1f;
        public byte R = 178, G = 205, B = 236;
        public int Fps = 30;

        public float SnowAmount = 1f;
        public float SnowSpeed = 1f;
        public float SnowSize = 1f;
        public float SnowOpacity = 0.9f;

        public State Clone() => (State)MemberwiseClone();
    }

    public IntPtr Handle => _hwnd;

    public Native.RECT CurrentRect => _rect;

    // ---------------------------------------------------------------- 窗口

    public void Create()
    {
        _procKeepAlive = WndProc;

        var wc = new Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_procKeepAlive),
            hInstance = Native.GetModuleHandleW(null),
            hbrBackground = IntPtr.Zero,
            lpszClassName = ClassName
        };
        Native.RegisterClassExW(ref wc);

        _rect = ComputeRect(false);

        int exStyle = Native.WS_EX_NOREDIRECTIONBITMAP
                    | Native.WS_EX_TRANSPARENT
                    | Native.WS_EX_LAYERED
                    | Native.WS_EX_TOOLWINDOW
                    | Native.WS_EX_NOACTIVATE;

        if (!_bottom) exStyle |= Native.WS_EX_TOPMOST;

        _hwnd = Native.CreateWindowExW(
            exStyle, ClassName, "DesktopRain", WS_POPUP,
            _rect.Left, _rect.Top,
            _rect.Right - _rect.Left, _rect.Bottom - _rect.Top,
            IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("创建窗口失败，错误码 " + Marshal.GetLastWin32Error());

        // 分层窗口 + WS_EX_TRANSPARENT：系统会整体跳过本窗口的命中测试，
        // 鼠标消息直接落到下层窗口（比单靠 WM_NCHITTEST 返回 HTTRANSPARENT 更硬，
        // 桌面图标、任务栏这类也点得到）。alpha=255 不改变画面。
        Native.SetLayeredWindowAttributes(_hwnd, 0, 255, Native.LWA_ALPHA);

        ApplyZOrder();

        Native.ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        ApplyZOrder();
    }

    /// <summary>当前层级：true = 压在桌面底层（所有普通窗口之下），false = 总在最上层。</summary>
    public void SetLayer(bool bottom)
    {
        if (_bottom == bottom) { ApplyZOrder(); return; }
        _bottom = bottom;
        ApplyZOrder();
        AppConfig.Log(bottom ? "层级: 桌面底层（雨在窗口下面，不参与任何点击）"
                             : "层级: 总在最上层");
    }

    private IntPtr ZOrderAfter => _bottom ? Native.HWND_BOTTOM : Native.HWND_TOPMOST;

    /// <summary>套用 z 序：底层用 HWND_BOTTOM（同时会自动丢掉 topmost 属性）。</summary>
    private void ApplyZOrder()
    {
        if (_hwnd == IntPtr.Zero) return;

        const int GWL_EXSTYLE = -20;
        const long WS_EX_TOPMOST = 0x00000008L;

        long style = Native.GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        style = _bottom ? (style & ~WS_EX_TOPMOST) : (style | WS_EX_TOPMOST);
        Native.SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(style));

        // 改过 GWL_EXSTYLE 后分层属性可能被重置，重新套一次（alpha=255 不影响画面）
        Native.SetLayeredWindowAttributes(_hwnd, 0, 255, Native.LWA_ALPHA);

        Native.SetWindowPos(_hwnd, ZOrderAfter, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private static Native.RECT ComputeRect(bool coverTaskbar)
    {
        return coverTaskbar ? Native.GetPrimaryMonitorRect() : Native.GetPrimaryWorkArea();
    }

    /// <summary>重新贴合主屏（分辨率/任务栏变化后调用）。</summary>
    public void Relayout(bool coverTaskbar)
    {
        var r = ComputeRect(coverTaskbar);
        _rect = r;
        if (_hwnd != IntPtr.Zero)
        {
            Native.SetWindowPos(_hwnd, ZOrderAfter,
                r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                Native.SWP_NOACTIVATE);
        }
        _needResize = true;
    }

    public void SetHidden(bool hidden)
    {
        if (_hidden == hidden) return;
        _hidden = hidden;
        UpdateVisibility();
        AppConfig.Log(hidden ? "特效: 检测到全屏应用，暂时隐藏" : "特效: 全屏应用退出，恢复显示");
    }

    /// <summary>
    /// 统一决定窗口是否存在：暂停（关掉雨效）或全屏时，窗口直接 SW_HIDE。
    /// 只要窗口还挂在桌面上就仍然参与命中测试，所以"不画雨"必须等价于"窗口不在"，
    /// 否则就会出现"雨关了但桌面/图标点不动"的问题。
    /// </summary>
    private void UpdateVisibility()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool show = !_paused && !_hidden;
        Native.ShowWindow(_hwnd, show ? SW_SHOWNOACTIVATE : 0 /* SW_HIDE */);
        if (show) ApplyZOrder(); // ShowWindow 会把窗口提到同层顶部，压回去
    }

    public void ApplyConfig(AppConfig cfg)
    {
        _state = new State
        {
            Enabled = true,
            Mode = cfg.SnowMode ? "snow" : "rain",
            Preset = cfg.PresetValue,
            Opacity = Math.Clamp(cfg.Opacity, 0.05f, 1f),
            Density = Math.Clamp(cfg.DensityScale, 0.1f, 3f),
            Speed = Math.Clamp(cfg.SpeedScale, 0.2f, 3f),
            Wind = cfg.Wind,
            Tail = Math.Clamp(cfg.TailFactor, 0.005f, 0.08f),
            Stroke = Math.Clamp(cfg.StrokeWidth, 0.5f, 4f),
            R = (byte)Math.Clamp(cfg.ColorR, 0, 255),
            G = (byte)Math.Clamp(cfg.ColorG, 0, 255),
            B = (byte)Math.Clamp(cfg.ColorB, 0, 255),
            Fps = Math.Clamp(cfg.TargetFps, 15, 120),
            SnowAmount = Math.Clamp(cfg.SnowAmount, 0.1f, 3f),
            SnowSpeed = Math.Clamp(cfg.SnowSpeed, 0.2f, 3f),
            SnowSize = Math.Clamp(cfg.SnowSize, 0.4f, 3f),
            SnowOpacity = Math.Clamp(cfg.SnowOpacity, 0.1f, 1f)
        };
        // 只重算参数，不重建粒子：拖滑杆时雨/雪是连续变化的，不会整屏闪一下
        _needApply = true;
    }

    /// <summary>暂停/恢复。暂停时窗口会被真正隐藏（而不是留一个透明窗口在桌面上）。</summary>
    public void SetPaused(bool paused)
    {
        State s = _state.Clone();
        s.Enabled = !paused;
        _state = s;
        _paused = paused;
        UpdateVisibility();
        if (!paused) _needResize = true;
    }

    // ---------------------------------------------------------------- 渲染线程

    public void Start()
    {
        _thread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "RainRender",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    private void RenderLoop()
    {
        Native.TimeBeginPeriod(1);
        try
        {
            _renderer = new GpuRainRenderer();
            _renderer.Initialize(_hwnd, _rect.Right - _rect.Left, _rect.Bottom - _rect.Top);
            AppConfig.Log($"渲染: {_renderer.PixelWidth}x{_renderer.PixelHeight} D3D11+D2D+DirectComposition 已就绪");

            ApplyEmitters(_state);
            _activeSnow = _state.Mode == "snow";
            PrimeActive(_activeSnow);
            LogParams(_state);

            double last = _clock.Elapsed.TotalSeconds;
            double next = last;
            int frames = 0;
            double fpsFrom = last;

            while (!_stop)
            {
                State s = _state;
                bool snow = s.Mode == "snow";

                if (_needResize)
                {
                    _needResize = false;
                    _needApply = false;
                    _renderer.Resize(_rect.Right - _rect.Left, _rect.Bottom - _rect.Top);
                    ApplyEmitters(s);
                    _activeSnow = snow;
                    PrimeActive(snow);
                    next = _clock.Elapsed.TotalSeconds;
                    LogParams(s);
                }
                else if (_needApply)
                {
                    // 调雨量 / 下落速度：保留现有粒子，只按比例重算，画面连续
                    _needApply = false;
                    ApplyEmitters(s);
                    LogParams(s);
                }

                // 雨↔雪 切换：换到另一套粒子，重新铺满屏幕
                if (snow != _activeSnow)
                {
                    _activeSnow = snow;
                    PrimeActive(snow);
                }

                if (!s.Enabled || _hidden)
                {
                    _renderer.ClearOnly();
                    Thread.Sleep(40);
                    last = _clock.Elapsed.TotalSeconds;
                    next = last;
                    frames = 0;
                    fpsFrom = last; // 隐藏期间不计入帧率统计
                    continue;
                }

                double now = _clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Clamp(now - last, 0.0, 0.1);
                last = now;

                int w = _renderer.PixelWidth;
                int h = _renderer.PixelHeight;

                if (snow)
                {
                    _snow.Step(dt, w, h);
                    _snow.Fill(w, h);
                    _renderer.DrawSnow(_snow.Items, s.SnowOpacity);
                }
                else
                {
                    _emitter.WindX = s.Wind;
                    _emitter.Step(dt, w, h);
                    _emitter.Fill(w, h);

                    var color = new Color4(s.R / 255f, s.G / 255f, s.B / 255f, s.Opacity);
                    _renderer.Draw(_emitter.Items, color, s.Stroke, s.Tail);
                }

                // 合成交换链由 DWM 按自身节拍取帧，不需要额外 DwmFlush，
                // 否则每帧多等一个 vblank，实测会把 30fps 拖到 25fps。
                frames++;
                if (now - fpsFrom > 10.0)
                {
                    int n = snow ? _snow.Items.Count : _emitter.Items.Count;
                    AppConfig.Log($"特效: {(snow ? "雪" : "雨")} 实测 {frames / (now - fpsFrom):0.0} fps，粒子 {n} 个");
                    frames = 0;
                    fpsFrom = now;
                }

                // 帧率节流
                next += 1.0 / s.Fps;
                double remain = next - _clock.Elapsed.TotalSeconds;
                if (remain > 0.002)
                    Thread.Sleep((int)Math.Min(30, (remain - 0.001) * 1000));
                int guard = 0;
                while (_clock.Elapsed.TotalSeconds < next && guard++ < 2000)
                    Thread.SpinWait(60);
                if (_clock.Elapsed.TotalSeconds > next + 0.25)
                    next = _clock.Elapsed.TotalSeconds;
            }
        }
        catch (Exception ex)
        {
            AppConfig.Log("渲染异常: " + ex);
        }
        finally
        {
            try { _renderer?.Dispose(); } catch { }
            Native.TimeEndPeriod(1);
        }
    }

    // ---------------------------------------------------------------- 消息

    // —— 输入穿透自检哨兵：正常情况下本窗口只会收到 WM_NCHITTEST（被询问一下），
    //    永远不该收到 MOUSEMOVE / 按键消息。一旦收到，说明穿透失效、会挡鼠标，必须记日志。 ——
    private static long _cMove, _cDown, _cUp;
    private static readonly Stopwatch _probeClock = Stopwatch.StartNew();
    private static double _probeLastLog;

    private static void ProbeCheck()
    {
        double t = _probeClock.Elapsed.TotalSeconds;
        if (t - _probeLastLog < 2.0) return;
        _probeLastLog = t;
        long move = Interlocked.Exchange(ref _cMove, 0);
        long down = Interlocked.Exchange(ref _cDown, 0);
        long up = Interlocked.Exchange(ref _cUp, 0);
        if (move + down + up == 0) return; // 正常：静默
        AppConfig.Log($"警告: 穿透可能失效，本窗口收到鼠标消息 MOVE={move} DOWN={down} UP={up}");
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0084: // WM_NCHITTEST
                return new IntPtr(-1); // HTTRANSPARENT：鼠标完全穿透

            case 0x0200: // WM_MOUSEMOVE
                Interlocked.Increment(ref _cMove);
                ProbeCheck();
                break;

            case 0x0201: // WM_LBUTTONDOWN
                Interlocked.Increment(ref _cDown);
                ProbeCheck();
                break;

            case 0x0202: // WM_LBUTTONUP
                Interlocked.Increment(ref _cUp);
                ProbeCheck();
                break;
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _stop = true;
        try { _thread?.Join(1500); } catch { }
        if (_hwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
