using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using EraCore.Hosting;
using EraCore.Native;
using SkiaSharp;

namespace EraCore.Native.Android;

/// <summary>
/// 原生文本终端画布：
/// - 用 Hosting <see cref="VirtualWindow"/> 计算可见区间/贴底/滚动条；
/// - 绘制时由 <see cref="NativeSkiaRenderer"/> 产出基于实际字体度量的按钮命中区；
/// - 轻点（非拖拽）命中按钮时触发 <see cref="ButtonActivated"/>；
/// - 拖拽同时支持纵向滚动与横向平移；
/// - 字号/行距变化时用「首可见行锚定」保持滚动位置不错行（贴底时继续贴底）。
/// </summary>
internal sealed class NativeGameView : Control
{
    private NativeFrame _frame = NativeFrame.Empty;
    private NativeRenderStyle _style = NativeRenderStyle.From(NativeDefaults.Metrics, AppSettings.Default);
    private readonly NativeImageCache _images;
    private readonly NativeImageLayoutCache _imageLayout = new();

    private double _scrollY;
    private double _scrollX;
    private bool _followBottom = true;
    private bool _dragging;
    private Point _lastPoint;
    private double _dragDistance;
    private IReadOnlyList<NativeHitRegion> _hitRegions = Array.Empty<NativeHitRegion>();
    private IReadOnlyList<NativeImageHitRegion> _imageRegions = Array.Empty<NativeImageHitRegion>();
    private long _version;

    public event Action<NativeButton>? ButtonActivated;

    /// <summary>含 srcm 的图片热区被点击（坐标 = 控件局部坐标，即点击点）。</summary>
    public event Action<NativeImageHitRegion, Point>? ImageActivated;

    public NativeGameView()
    {
        ClipToBounds = true;
        Focusable = true;
        _images = new NativeImageCache(OnImagesChanged);
    }

    private void OnImagesChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _version++;
            InvalidateVisual();
        });
    }

    /// <summary>由 UI 轮询线程调用：替换当前帧与渲染参数（不可变对象），并按贴底/锚定规则重算滚动位置。</summary>
    public void SetFrame(NativeFrame frame, NativeRenderStyle style)
    {
        var viewportHeight = Bounds.Height;
        var viewportWidth = Bounds.Width;
        var hasLayout = viewportHeight > 0;
        var oldMaxY = hasLayout ? MaxScrollY(viewportHeight) : 0;
        var wasAtBottom = !hasLayout || _followBottom || oldMaxY <= 0 || _scrollY >= oldMaxY - 2;
        var lineHeightChanged = Math.Abs(style.LineHeight - _style.LineHeight) > 0.01;

        // 改字号前记录首可见行；改完后按新行高还原该行，避免跳到别的行或直接贴底。
        var anchorIndex = -1;
        if (hasLayout && !wasAtBottom && lineHeightChanged && _frame.Lines.Count > 0)
        {
            anchorIndex = VirtualWindow.Compute(
                _frame.Lines.Count,
                _scrollY,
                viewportHeight,
                new VirtualWindowOptions(_style.LineHeight, Overscan: 0)).StartIndex;
        }

        _images.UpdateSource(NativeRuntime.Files);
        _frame = frame;
        _style = style;

        if (hasLayout)
        {
            if (wasAtBottom)
            {
                _scrollY = MaxScrollY(viewportHeight);
            }
            else if (anchorIndex >= 0)
            {
                _scrollY = Math.Clamp(
                    VirtualWindow.OffsetOf(
                        Math.Min(anchorIndex, frame.Lines.Count),
                        frame.Lines.Count,
                        new VirtualWindowOptions(style.LineHeight)),
                    0,
                    MaxScrollY(viewportHeight));
            }
            else
            {
                _scrollY = Math.Clamp(_scrollY, 0, MaxScrollY(viewportHeight));
            }
            _scrollX = Math.Clamp(_scrollX, 0, MaxScrollX(viewportWidth));
        }

        _followBottom = wasAtBottom;
        _version++;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return;
        _images.PrefetchWindow(_frame, _scrollY, size.Height, _style.LineHeight, _style.Scale);
        context.Custom(new GameDrawOp(
            new Rect(size), _frame, _style, _images, _imageLayout, _scrollX, _scrollY, _version,
            hits =>
            {
                _hitRegions = hits.Buttons;
                _imageRegions = hits.Images;
            }));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (_followBottom)
            _scrollY = MaxScrollY(finalSize.Height);
        _scrollY = Math.Clamp(_scrollY, 0, MaxScrollY(finalSize.Height));
        _scrollX = Math.Clamp(_scrollX, 0, MaxScrollX(finalSize.Width));
        return result;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
            return;

        _dragging = true;
        _lastPoint = point.Position;
        _dragDistance = 0;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
            return;

        var position = e.GetPosition(this);
        var dx = position.X - _lastPoint.X;
        var dy = position.Y - _lastPoint.Y;
        _lastPoint = position;
        _dragDistance += Math.Abs(dx) + Math.Abs(dy);

        _scrollY = Math.Clamp(_scrollY - dy, 0, MaxScrollY(Bounds.Height));
        _scrollX = Math.Clamp(_scrollX - dx, 0, MaxScrollX(Bounds.Width));
        _followBottom = IsAtBottom();
        _version++;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
            return;

        // 轻点（非拖拽）→ 按钮命中；用绘制阶段产出的实际 run 位置，而非列×FontSize/2。
        // srcm 图片热区优先：与 Web 一致，命中热区后由热区接管（取色失败也不再提交按钮原值）。
        if (_dragDistance < 8)
        {
            var position = e.GetPosition(this);
            // 逆序：热区列表按绘制顺序追加，最后一个命中的才是视觉最上层的那张图。
            for (var i = _imageRegions.Count - 1; i >= 0; i--)
            {
                var region = _imageRegions[i];
                if (!region.Contains(position))
                    continue;
                ImageActivated?.Invoke(region, position);
                _dragging = false;
                e.Pointer.Capture(null);
                _followBottom = IsAtBottom();
                e.Handled = true;
                return;
            }

            foreach (var region in _hitRegions)
            {
                if (!region.Contains(position))
                    continue;
                ButtonActivated?.Invoke(region.Button);
                break;
            }
        }

        _dragging = false;
        e.Pointer.Capture(null);
        _followBottom = IsAtBottom();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var step = Math.Max(1, _style.LineHeight) * 3;
        _scrollY = Math.Clamp(_scrollY - (e.Delta.Y * step), 0, MaxScrollY(Bounds.Height));
        _followBottom = IsAtBottom();
        _version++;
        InvalidateVisual();
        e.Handled = true;
    }

    private bool IsAtBottom()
    {
        var max = MaxScrollY(Bounds.Height);
        return max <= 0 || _scrollY >= max - 2;
    }

    /// <summary>
    /// srcm 像素取色：把控件局部点击坐标按 Web 的 <c>clickToImagePixel</c> 语义反算回
    /// 显示图的自然像素坐标，再读映射图（srcm）同坐标像素，返回 0xRRGGBB。
    ///
    /// <para>映射基准是**显示图 &lt;img&gt; 元素矩形**（有 crop 时是整图缩放后的矩形），
    /// 与 Web 的 <c>img.getBoundingClientRect()</c> 完全对应：ypos / 行内游标 / 缩放都已在
    /// 元素矩形里折进去；crop 的负偏移体现在元素矩形的起点。</para>
    ///
    /// <para>失败原因分开返回（未解码 / 越界 / 透明），调用方据此写不同日志并拒绝提交。</para>
    /// </summary>
    public bool TryReadSrcmPixel(NativeImageHitRegion region, Point point, out int rgb, out string failure)
    {
        rgb = 0;

        var display = _images.Get(region.Src);
        if (display.State != NativeImageState.Ready || display.Bitmap is not { } displayBitmap)
        {
            failure = $"display image not decoded (state={display.State}, detail={display.Detail ?? "none"})";
            return false;
        }

        if (!region.ContainsElement(point))
        {
            failure = "point outside image element rect";
            return false;
        }

        if (!region.ContainsVisible(point))
        {
            failure = "point outside crop window";
            return false;
        }

        // Web：Math.floor(offset / rect.size * naturalSize)，再钳到 [0, natural-1]。
        var offsetX = (point.X - region.ElementX) / region.ElementWidth * displayBitmap.Width;
        var offsetY = (point.Y - region.ElementY) / region.ElementHeight * displayBitmap.Height;
        var pixelX = Math.Clamp((int)Math.Floor(offsetX), 0, displayBitmap.Width - 1);
        var pixelY = Math.Clamp((int)Math.Floor(offsetY), 0, displayBitmap.Height - 1);

        var mapping = _images.Get(region.Srcm);
        if (mapping.State != NativeImageState.Ready || mapping.Bitmap is not { } mapBitmap)
        {
            failure = $"srcm not decoded (state={mapping.State}, detail={mapping.Detail ?? "none"})";
            return false;
        }

        if (pixelX >= mapBitmap.Width || pixelY >= mapBitmap.Height)
        {
            failure =
                $"mapped pixel ({pixelX},{pixelY}) outside srcm {mapBitmap.Width}x{mapBitmap.Height}";
            return false;
        }

        if (!_images.TryReadPixel(region.Srcm, pixelX, pixelY, out var color, out var detail))
        {
            failure = $"srcm pixel ({pixelX},{pixelY}) unavailable: {detail}";
            return false;
        }

        if (color.Alpha == 0)
        {
            failure = $"transparent pixel at ({pixelX},{pixelY})";
            return false;
        }

        rgb = (color.Red << 16) | (color.Green << 8) | color.Blue;
        failure = string.Empty;
        return true;
    }

    private double MaxScrollY(double viewportHeight)
        => VirtualWindow.Compute(
            _frame.Lines.Count,
            0,
            viewportHeight,
            new VirtualWindowOptions(_style.LineHeight)).MaxScrollY;

    private double MaxScrollX(double viewportWidth)
        => Math.Max(0, _style.ContentWidth - viewportWidth);

    private sealed class GameDrawOp : ICustomDrawOperation
    {
        private readonly NativeFrame _frame;
        private readonly NativeRenderStyle _style;
        private readonly NativeImageCache _images;
        private readonly NativeImageLayoutCache _imageLayout;
        private readonly double _scrollX;
        private readonly double _scrollY;
        private readonly long _version;
        private readonly Action<NativeDrawHits> _onHitRegions;

        public GameDrawOp(
            Rect bounds,
            NativeFrame frame,
            NativeRenderStyle style,
            NativeImageCache images,
            NativeImageLayoutCache imageLayout,
            double scrollX,
            double scrollY,
            long version,
            Action<NativeDrawHits> onHitRegions)
        {
            Bounds = bounds;
            _frame = frame;
            _style = style;
            _images = images;
            _imageLayout = imageLayout;
            _scrollX = scrollX;
            _scrollY = scrollY;
            _version = version;
            _onHitRegions = onHitRegions;
        }

        public Rect Bounds { get; }

        public void Dispose()
        {
        }

        public bool Equals(ICustomDrawOperation? other)
            => other is GameDrawOp op && op._version == _version && op.Bounds == Bounds;

        public bool HitTest(Point p) => Bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null)
                return;

            using var lease = feature.Lease();
            var canvas = lease?.SkCanvas;
            if (canvas is null)
                return;

            var regions = NativeSkiaRenderer.Draw(
                canvas,
                Bounds.Width,
                Bounds.Height,
                _frame,
                _style,
                _images,
                _scrollX,
                _scrollY,
                _imageLayout);
            _onHitRegions(regions);
        }
    }
}
