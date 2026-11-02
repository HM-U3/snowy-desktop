using System;
using System.Collections.Generic;
using System.Numerics;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DesktopRain;

/// <summary>
/// GPU 渲染管线：D3D11 → DXGI 合成交换链 → DirectComposition → Direct2D。
/// 相比原版 WPF 的 AllowsTransparency（强制软件渲染，3072×1920 只能跑到个位数帧率），
/// 这条链路由 GPU 直接合成，全屏雨滴也不掉帧。
/// </summary>
internal sealed class GpuRainRenderer : IDisposable
{
    private ID3D11Device _device;
    private ID3D11DeviceContext _context;
    private IDXGIFactory2 _factory;
    private IDXGISwapChain1 _swapChain;
    private ID2D1Device _d2dDevice;
    private ID2D1DeviceContext _dc;
    private ID2D1Bitmap1 _targetBitmap;
    private ID2D1SolidColorBrush _brush;
    private IDCompositionDevice _dcomp;
    private IDCompositionTarget _compTarget;
    private IDCompositionVisual _visual;

    /// <summary>雪花贴图：白色径向渐变，边缘完全化开，叠起来就是蓬松的一团。</summary>
    private ID2D1Bitmap1 _flakeTex;
    private const int FlakeTexSize = 64;

    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }

    /// <summary>创建整套 GPU 资源并绑定到指定窗口。</summary>
    public void Initialize(IntPtr hwnd, int width, int height)
    {
        PixelWidth = Math.Max(1, width);
        PixelHeight = Math.Max(1, height);

        var levels = new Vortice.Direct3D.FeatureLevel[]
        {
            Vortice.Direct3D.FeatureLevel.Level_11_1,
            Vortice.Direct3D.FeatureLevel.Level_11_0,
            Vortice.Direct3D.FeatureLevel.Level_10_1,
            Vortice.Direct3D.FeatureLevel.Level_10_0
        };

        Result result = D3D11.D3D11CreateDevice(
            (IDXGIAdapter)null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            levels,
            out _device,
            out _context);

        if (result.Failure)
            throw new InvalidOperationException("D3D11 设备创建失败: " + result.Description);

        using IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        _factory = adapter.GetParent<IDXGIFactory2>();

        // Direct2D
        _d2dDevice = D2D1.D2D1CreateDevice(dxgiDevice, null);
        _dc = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _dc.AntialiasMode = AntialiasMode.PerPrimitive;
        _dc.UnitMode = UnitMode.Pixels;

        // 合成交换链（预乘 alpha，供 DirectComposition 使用）
        var desc = new SwapChainDescription1
        {
            Width = (uint)PixelWidth,
            Height = (uint)PixelHeight,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
            Flags = SwapChainFlags.None
        };

        _swapChain = _factory.CreateSwapChainForComposition(_device, desc, null);

        CreateTargetBitmap();

        // DirectComposition：把交换链挂到窗口上
        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _dcomp.CreateTargetForHwnd(hwnd, true, out _compTarget);
        _visual = _dcomp.CreateVisual();
        _visual.SetContent(_swapChain);
        _compTarget.SetRoot(_visual);
        _dcomp.Commit();

        CreateFlakeTexture();
    }

    /// <summary>
    /// 预生成一张 64×64 的雪花贴图。alpha = (1-r²)^1.8：中间是实心颗粒、边缘在 2~3 像素内化开，
    /// 这样缩小后是一颗颗有轮廓的雪粒，而不是一团朦胧的白雾。
    /// 每帧只需 DrawBitmap，不必为每片雪花创建径向渐变刷。
    /// </summary>
    private unsafe void CreateFlakeTexture()
    {
        var data = new byte[FlakeTexSize * FlakeTexSize * 4];
        float half = FlakeTexSize * 0.5f;

        for (int y = 0; y < FlakeTexSize; y++)
        {
            for (int x = 0; x < FlakeTexSize; x++)
            {
                float dx = (x + 0.5f) / half - 1f;
                float dy = (y + 0.5f) / half - 1f;
                float r2 = dx * dx + dy * dy;
                float a = r2 >= 1f ? 0f : (float)Math.Pow(1f - r2, 1.8f);
                byte v = (byte)Math.Clamp(a * 255f, 0f, 255f);

                int i = (y * FlakeTexSize + x) * 4;
                data[i] = v;      // B
                data[i + 1] = v;  // G
                data[i + 2] = v;  // R
                data[i + 3] = v;  // A（预乘）
            }
        }

        var props = new BitmapProperties1
        {
            PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            DpiX = 96f,
            DpiY = 96f,
            BitmapOptions = BitmapOptions.None
        };

        fixed (byte* p = data)
        {
            _flakeTex = _dc.CreateBitmap(
                new SizeI(FlakeTexSize, FlakeTexSize),
                (IntPtr)p,
                FlakeTexSize * 4u,
                props);
        }
    }

    private void CreateTargetBitmap()
    {
        _targetBitmap?.Dispose();
        using IDXGISurface surface = _swapChain.GetBuffer<IDXGISurface>(0);
        var props = new BitmapProperties1
        {
            PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            DpiX = 96f,
            DpiY = 96f,
            BitmapOptions = BitmapOptions.Target | BitmapOptions.CannotDraw
        };
        _targetBitmap = _dc.CreateBitmapFromDxgiSurface(surface, props);
        _dc.Target = _targetBitmap;
    }

    /// <summary>分辨率变化时重建交换链缓冲。</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == PixelWidth && height == PixelHeight) return;

        _dc.Target = null;
        _targetBitmap?.Dispose();
        _targetBitmap = null;

        _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        PixelWidth = width;
        PixelHeight = height;
        CreateTargetBitmap();
        _dcomp.Commit();
    }

    /// <summary>每帧入口：清空为全透明，画雨丝，提交。</summary>
    public void Draw(IReadOnlyList<Drop> drops, Color4 color, float strokeWidth, float tailFactor)
    {
        _dc.BeginDraw();
        _dc.Clear(new Color4(0f, 0f, 0f, 0f));

        if (_brush == null)
            _brush = _dc.CreateSolidColorBrush(color, null);
        else
            _brush.Color = color;

        // 雨丝：一条短线，尾巴指向运动反方向。长度 = tailFactor × 速度（原版 0.022）
        for (int i = 0; i < drops.Count; i++)
        {
            Drop d = drops[i];
            var head = new Vector2(d.X, d.Y);
            var tail = new Vector2(d.X - d.Vx * tailFactor, d.Y - d.Vy * tailFactor);
            _dc.DrawLine(head, tail, _brush, strokeWidth);
        }

        _dc.EndDraw();
        _swapChain.Present(0, PresentFlags.None);
    }

    /// <summary>
    /// 画雪花：每片一张软边贴图，透明度按“片自身 alpha × 全局不透明度”。
    /// 贴图半径 1 对应可见半径约 0.65，所以目标框取 3.1×R，视觉大小才和设定值一致。
    /// </summary>
    public void DrawSnow(IReadOnlyList<Flake> flakes, float globalOpacity)
    {
        _dc.BeginDraw();
        _dc.Clear(new Color4(0f, 0f, 0f, 0f));

        // 注意：Vortice 的 Rect 是 (x, y, 宽, 高)，不是 (左, 上, 右, 下)
        var src = new Rect(0f, 0f, FlakeTexSize, FlakeTexSize);

        for (int i = 0; i < flakes.Count; i++)
        {
            Flake f = flakes[i];
            float size = f.R * 3.1f;
            var dst = new Rect(f.X - size * 0.5f, f.Y - size * 0.5f, size, size);
            float opacity = Math.Clamp(f.Alpha * globalOpacity, 0f, 1f);
            _dc.DrawBitmap(_flakeTex, dst, opacity, Vortice.Direct2D1.BitmapInterpolationMode.Linear, src);
        }

        _dc.EndDraw();
        _swapChain.Present(0, PresentFlags.None);
    }

    /// <summary>只清屏（暂停或隐藏时用）。</summary>
    public void ClearOnly()
    {
        _dc.BeginDraw();
        _dc.Clear(new Color4(0f, 0f, 0f, 0f));
        _dc.EndDraw();
        _swapChain.Present(0, PresentFlags.None);
    }

    public void Dispose()
    {
        try { _compTarget?.Dispose(); } catch { }
        try { _visual?.Dispose(); } catch { }
        try { _dcomp?.Dispose(); } catch { }
        try { _targetBitmap?.Dispose(); } catch { }
        try { _flakeTex?.Dispose(); } catch { }
        try { _brush?.Dispose(); } catch { }
        try { _dc?.Dispose(); } catch { }
        try { _d2dDevice?.Dispose(); } catch { }
        try { _swapChain?.Dispose(); } catch { }
        try { _factory?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
    }
}
