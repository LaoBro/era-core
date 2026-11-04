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

    private double _scrollY;
    private double _scrollX;
    private bool _followBottom = true;
    private bool _dragging;
    private Point _lastPoint;
    private double _dragDistance;
    private IReadOnlyList<NativeHitRegion> _hitRegions = Array.Empty<NativeHitRegion>();
    private long _version;

    public event Action<NativeButton>? ButtonActivated;

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
        _images.PrefetchWindow(_frame, _scrollY, size.Height, _style.LineHeight);
        context.Custom(new GameDrawOp(
            new Rect(size), _frame, _style, _images, _scrollX, _scrollY, _version,
            regions => _hitRegions = regions));
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
        if (_dragDistance < 8)
        {
            var position = e.GetPosition(this);
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
        private readonly double _scrollX;
        private readonly double _scrollY;
        private readonly long _version;
        private readonly Action<IReadOnlyList<NativeHitRegion>> _onHitRegions;

        public GameDrawOp(
            Rect bounds,
            NativeFrame frame,
            NativeRenderStyle style,
            NativeImageCache images,
            double scrollX,
            double scrollY,
            long version,
            Action<IReadOnlyList<NativeHitRegion>> onHitRegions)
        {
            Bounds = bounds;
            _frame = frame;
            _style = style;
            _images = images;
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
                _scrollY);
            _onHitRegions(regions);
        }
    }
}
