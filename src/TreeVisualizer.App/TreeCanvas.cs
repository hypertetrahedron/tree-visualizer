using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using TreeVisualizer.Core.Drawing;
using TreeVisualizer.Core.Layout;
using TreeVisualizer.Core.Render;

namespace TreeVisualizer.App;

/// <summary>Interactive Skia surface: draws a scene and supports pan, zoom-at-cursor, hover and selection.</summary>
public sealed class TreeCanvas : Control
{
    private LayoutResult? _layout;
    private SkiaRenderer? _renderer;
    private ViewTransform _view;
    private bool _hasView;
    private Point? _dragStart;
    private ViewTransform _dragView;
    private bool _dragged;

    public LayoutResult? Layout => _layout;
    public LayoutNode? Hovered { get; private set; }
    public LayoutNode? Selected { get; private set; }
    public LabelSettings Labels { get; set; } = new();
    public IReadOnlySet<string> Highlights { get; set; } = new HashSet<string>();
    /// <summary>Id of a node hovered in a linked view; drawn with a hover ring here too.</summary>
    public string? LinkedId { get; set; }
    public ViewTransform View => _view;

    public event Action<LayoutNode?>? HoverChanged;
    /// <summary>Raised when the user pans, zooms or the view is refit (not by <see cref="SetView"/>).</summary>
    public event Action<ViewTransform>? ViewChanged;
    public event Action<LayoutNode?>? SelectionChanged;
    public event Action<LayoutNode>? NodeActivated;

    public TreeCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>Replaces what is drawn. If <paramref name="resetView"/>, fits the whole scene.</summary>
    public void SetScene(LayoutResult layout, Scene scene, bool resetView)
    {
        _layout = layout;
        _renderer = new SkiaRenderer(scene); // previous renderer is left to the GC; a frame may still be using it
        if (resetView || !_hasView) FitToView();
        if (Selected != null) Selected = layout.ById.GetValueOrDefault(Selected.Node.Id);
        Hovered = null;
        InvalidateVisual();
    }

    public void FitToView()
    {
        if (_renderer == null || Bounds.Width <= 0) { _hasView = false; return; }
        _view = ViewTransform.Fit(_renderer.Scene, Bounds.Width, Bounds.Height, 12);
        _hasView = true;
        InvalidateVisual();
        ViewChanged?.Invoke(_view);
    }

    /// <summary>Applies a view from a linked canvas.</summary>
    public void SetView(ViewTransform view)
    {
        _view = view;
        _hasView = _renderer != null;
        InvalidateVisual();
    }

    /// <summary>Centers and zooms on a node.</summary>
    public void ZoomTo(LayoutNode node, double zoomFactor = 6)
    {
        if (_renderer == null || _layout == null) return;
        var (wx, wy) = WorldPosition(node);
        var fit = ViewTransform.Fit(_renderer.Scene, Bounds.Width, Bounds.Height, 12);
        double s = Math.Max(_view.Scale, fit.Scale * zoomFactor);
        _view = new ViewTransform(s, Bounds.Width / 2 - wx * s, Bounds.Height / 2 - wy * s);
        Select(node);
        InvalidateVisual();
        ViewChanged?.Invoke(_view);
    }

    public void Select(LayoutNode? node)
    {
        if (ReferenceEquals(Selected, node)) return;
        Selected = node;
        SelectionChanged?.Invoke(node);
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_hasView) FitToView();
        else
        {
            // Keep the center of the view in the center.
            _view = _view with
            {
                OffsetX = _view.OffsetX + (e.NewSize.Width - e.PreviousSize.Width) / 2,
                OffsetY = _view.OffsetY + (e.NewSize.Height - e.PreviousSize.Height) / 2,
            };
        }
    }

    public override void Render(DrawingContext context)
    {
        if (_renderer == null || !_hasView)
        {
            context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
            return;
        }
        var markers = new List<(double X, double Y, double R, SKColor Color, float Width)>();
        void Mark(LayoutNode? n, SKColor color, float width, double extra)
        {
            if (n == null) return;
            var (wx, wy) = WorldPosition(n);
            var (sx, sy) = _view.ToScreen(wx, wy);
            markers.Add((sx, sy, 7 + extra, color, width));
        }
        if (Highlights.Count is > 0 and <= 2000 && _layout != null)
            foreach (var id in Highlights)
                if (_layout.ById.TryGetValue(id, out var n)) Mark(n, new SKColor(0xD0, 0x1C, 0x1C), 1.5f, 0);
        Mark(Selected, new SKColor(0x11, 0x11, 0x11), 2.5f, 2);
        if (LinkedId != null && _layout != null && _layout.ById.TryGetValue(LinkedId, out var linked))
            Mark(linked, new SKColor(0x1F, 0x6F, 0xEB), 2f, 0);
        Mark(Hovered, new SKColor(0x1F, 0x6F, 0xEB), 2f, 0);

        context.Custom(new DrawOp(new Rect(Bounds.Size), _renderer, _view, Labels, Highlights, markers));
    }

    private (double X, double Y) WorldPosition(LayoutNode n)
    {
        var layout = _layout!;
        double R = _renderer!.Scene.Radius;
        if (layout.IsNodeLink)
        {
            double rn = layout.NodeRadius(n) * R;
            return (rn * Math.Cos(n.NodeAngle), rn * Math.Sin(n.NodeAngle));
        }
        double r = n.Level == 0 ? 0 : (layout.RingBounds(n.Level).Inner + layout.RingBounds(n.Level).Outer) / 2 * R;
        return (r * Math.Cos(n.Angle), r * Math.Sin(n.Angle));
    }

    private LayoutNode? HitTest(Point p)
    {
        if (_layout == null || _renderer == null) return null;
        var (wx, wy) = _view.ToWorld(p.X, p.Y);
        double r = Math.Sqrt(wx * wx + wy * wy) / _renderer.Scene.Radius;
        if (r > 1 + _layout.RingWidth * 0.6) return null;
        return _layout.HitTest(r, Math.Atan2(wy, wx));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;
        _dragStart = pt.Position;
        _dragView = _view;
        _dragged = false;
        e.Pointer.Capture(this);
        Focus();
        if (e.ClickCount == 2 && HitTest(pt.Position) is { } node)
        {
            _dragStart = null;
            NodeActivated?.Invoke(node);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_dragStart is Point start)
        {
            var d = p - start;
            if (Math.Abs(d.X) + Math.Abs(d.Y) > 3) _dragged = true;
            if (_dragged)
            {
                _view = _dragView with { OffsetX = _dragView.OffsetX + d.X, OffsetY = _dragView.OffsetY + d.Y };
                InvalidateVisual();
                ViewChanged?.Invoke(_view);
            }
            return;
        }
        var hit = HitTest(p);
        if (!ReferenceEquals(hit, Hovered))
        {
            Hovered = hit;
            HoverChanged?.Invoke(hit);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart != null && !_dragged) Select(HitTest(e.GetPosition(this)));
        _dragStart = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (Hovered != null) { Hovered = null; HoverChanged?.Invoke(null); InvalidateVisual(); }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!_hasView) return;
        var p = e.GetPosition(this);
        double factor = Math.Pow(1.2, e.Delta.Y);
        double s = Math.Clamp(_view.Scale * factor, 1e-4, 1e3);
        var (wx, wy) = _view.ToWorld(p.X, p.Y);
        _view = new ViewTransform(s, p.X - wx * s, p.Y - wy * s);
        InvalidateVisual();
        ViewChanged?.Invoke(_view);
        e.Handled = true;
    }

    private sealed class DrawOp : ICustomDrawOperation
    {
        private readonly SkiaRenderer _renderer;
        private readonly ViewTransform _view;
        private readonly LabelSettings _labels;
        private readonly IReadOnlySet<string> _highlights;
        private readonly List<(double X, double Y, double R, SKColor Color, float Width)> _markers;

        public DrawOp(Rect bounds, SkiaRenderer renderer, ViewTransform view, LabelSettings labels,
            IReadOnlySet<string> highlights, List<(double, double, double, SKColor, float)> markers)
        {
            Bounds = bounds; _renderer = renderer; _view = view; _labels = labels; _highlights = highlights; _markers = markers;
        }

        public Rect Bounds { get; }
        public bool HitTest(Point p) => Bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature == null) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            _renderer.Draw(canvas, _view, (float)Bounds.Width, (float)Bounds.Height, _labels, null, _highlights);
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
            foreach (var m in _markers)
            {
                paint.Color = m.Color;
                paint.StrokeWidth = m.Width;
                canvas.DrawCircle((float)m.X, (float)m.Y, (float)m.R, paint);
            }
            canvas.Restore();
        }
    }
}
