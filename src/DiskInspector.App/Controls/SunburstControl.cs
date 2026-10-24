using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskInspector.App.Services;
using DiskInspector.Core.Analysis;
using DiskInspector.Core.Layout;
using DiskInspector.Core.Model;

namespace DiskInspector.App.Controls;

/// <summary>
/// Sunburst: the current folder in the middle, its contents in rings around it, each slice sized by space.
/// Click selects, double-click a folder to make it the center, click the center to go up.
/// </summary>
public sealed class SunburstControl : FrameworkElement
{
    public const int Rings = 6;

    public static readonly DependencyProperty TreeProperty = DependencyProperty.Register(nameof(Tree), typeof(ScanTree), typeof(SunburstControl),
        new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty RootNodeProperty = DependencyProperty.Register(nameof(RootNode), typeof(int), typeof(SunburstControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnInputChanged));

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(nameof(SelectedNode), typeof(int), typeof(SunburstControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoveredNodeProperty = DependencyProperty.Register(nameof(HoveredNode), typeof(int), typeof(SunburstControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(nameof(Metric), typeof(SizeMetric), typeof(SunburstControl),
        new PropertyMetadata(SizeMetric.Logical, OnInputChanged));

    public static readonly DependencyProperty ColorsProperty = DependencyProperty.Register(nameof(Colors), typeof(CategoryBreakdown), typeof(SunburstControl),
        new PropertyMetadata(null, OnInputChanged));

    public ScanTree? Tree { get => (ScanTree?)GetValue(TreeProperty); set => SetValue(TreeProperty, value); }
    public int RootNode { get => (int)GetValue(RootNodeProperty); set => SetValue(RootNodeProperty, value); }
    public int SelectedNode { get => (int)GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }
    public int HoveredNode { get => (int)GetValue(HoveredNodeProperty); set => SetValue(HoveredNodeProperty, value); }
    public SizeMetric Metric { get => (SizeMetric)GetValue(MetricProperty); set => SetValue(MetricProperty, value); }
    public CategoryBreakdown? Colors { get => (CategoryBreakdown?)GetValue(ColorsProperty); set => SetValue(ColorsProperty, value); }

    IReadOnlyList<SunburstSegment> _segments = [];
    int _segmentsRoot = -1;
    DrawingGroup? _drawing;
    Size _drawingSize;
    int _version;
    SunburstSegment? _hover;

    public SunburstControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SizeChanged += (_, _) =>
        {
            _drawing = null;
            InvalidateVisual();
        };
    }

    static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SunburstControl)d).Recompute();

    void Recompute()
    {
        var tree = Tree;
        if (tree is null || !tree.IsComplete || Colors is null)
        {
            _segments = [];
            _drawing = null;
            InvalidateVisual();
            return;
        }
        int root = RootNode is >= 0 and var r && r < tree.Count && tree.IsDirectory(r) ? r : ScanTree.RootId;
        var metric = Metric;
        int version = ++_version;
        Task.Run(() => SunburstLayout.Compute(tree, root, metric, Rings, 0.6)).ContinueWith(t =>
        {
            if (version != _version)
                return;
            if (t.IsFaulted)
            {
                Log.Error("Sunburst layout failed", t.Exception);
                return;
            }
            _segments = t.Result;
            _segmentsRoot = root;
            _drawing = null;
            _hover = null;
            InvalidateVisual();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    (Point Center, double Hole, double Ring) RingGeometry()
    {
        double radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 8);
        double ring = radius / (Rings + 1.4);
        return (new Point(ActualWidth / 2, ActualHeight / 2), ring * 1.4, ring);
    }

    DrawingGroup BuildDrawing()
    {
        var (c, hole, ring) = RingGeometry();
        var group = new DrawingGroup();
        var tree = Tree!;
        var colors = Colors!;
        var bg = BrushCache.Theme(this, "SolidBackgroundFillColorBaseBrush", Brushes.White);
        var pen = new Pen(bg, 1);
        pen.Freeze();
        var otherBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128));
        otherBrush.Freeze();
        using (var dc = group.Open())
        {
            foreach (var s in _segments)
            {
                double inner = hole + (s.Ring - 1) * ring, outer = inner + ring;
                Brush fill = s.IsOther ? otherBrush : SegmentBrush(tree, colors, s.Node);
                dc.DrawGeometry(fill, s.Sweep > 0.8 ? pen : null, ArcGeometry.Segment(c, inner, outer, s.StartAngle, s.Sweep));
            }
        }
        group.Freeze();
        return group;
    }

    /// <summary>Files use their category color; folders the color of what they mostly contain, a little darker.</summary>
    static Brush SegmentBrush(ScanTree tree, CategoryBreakdown colors, int node)
    {
        uint rgb = colors.ColorOf(node);
        if (tree.IsDirectory(node))
        {
            rgb = (uint)(((rgb >> 16 & 0xFF) * 82 / 100) << 16 | ((rgb >> 8 & 0xFF) * 82 / 100) << 8 | (rgb & 0xFF) * 82 / 100);
        }
        return BrushCache.Get(rgb);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var tree = Tree;
        if (tree is null || ActualWidth < 40 || ActualHeight < 40)
            return;
        if (_drawing is null || _drawingSize != RenderSize)
        {
            _drawing = BuildDrawing();
            _drawingSize = RenderSize;
        }
        dc.DrawDrawing(_drawing);

        var (c, hole, ring) = RingGeometry();
        var fg = BrushCache.Theme(this, "TextFillColorPrimaryBrush", Brushes.Black);
        var fg2 = BrushCache.Theme(this, "TextFillColorSecondaryBrush", Brushes.Gray);
        var centerFill = BrushCache.Theme(this, "CardBackgroundFillColorDefaultBrush", Brushes.WhiteSmoke);
        dc.DrawEllipse(centerFill, new Pen(BrushCache.Theme(this, "CardStrokeColorDefaultBrush", Brushes.LightGray), 1), c, hole - 3, hole - 3);

        if (_hover is { } h)
        {
            double inner = hole + (h.Ring - 1) * ring;
            var hl = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
            dc.DrawGeometry(hl, new Pen(fg, 1.5), ArcGeometry.Segment(c, inner, inner + ring, h.StartAngle, h.Sweep));
        }
        foreach (var s in _segments)
            if (!s.IsOther && s.Node == SelectedNode)
            {
                double inner = hole + (s.Ring - 1) * ring;
                dc.DrawGeometry(null, SelectPen, ArcGeometry.Segment(c, inner, inner + ring, s.StartAngle, s.Sweep));
            }

        // Center caption: the folder shown and its size (or the hovered slice).
        int root = _segmentsRoot >= 0 && _segmentsRoot < tree.Count ? _segmentsRoot : 0;
        string title = root == 0 ? tree.RootPath : tree.Name(root);
        long size = tree.GetSize(root, Metric);
        string sub = Format.Bytes(size) + (root > 0 ? "\nclick to go up" : "");
        if (_hover is { } hv)
        {
            title = hv.IsOther ? $"{hv.OtherCount:N0} smaller items" : tree.Name(hv.Node);
            sub = $"{Format.Bytes(hv.Size)}  ·  {Format.Percent(size > 0 ? (double)hv.Size / size : 0)}";
        }
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var t1 = new FormattedText(title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            Math.Clamp(hole * 0.2, 11, 22), fg, dpi)
        { MaxTextWidth = hole * 1.7, MaxLineCount = 2, TextAlignment = TextAlignment.Center, Trimming = TextTrimming.CharacterEllipsis };
        var t2 = new FormattedText(sub, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"),
            Math.Clamp(hole * 0.13, 10, 15), fg2, dpi)
        { MaxTextWidth = hole * 1.6, MaxLineCount = 3, TextAlignment = TextAlignment.Center, Trimming = TextTrimming.CharacterEllipsis };
        double th = t1.Height + t2.Height + 4;
        dc.DrawText(t1, new Point(c.X - t1.MaxTextWidth / 2, c.Y - th / 2));
        dc.DrawText(t2, new Point(c.X - t2.MaxTextWidth / 2, c.Y - th / 2 + t1.Height + 4));
    }

    static readonly Pen SelectPen = CreateSelectPen();

    static Pen CreateSelectPen()
    {
        var p = new Pen(Brushes.Yellow, 3);
        p.Freeze();
        return p;
    }

    /// <summary>-2 for the center, the segment otherwise, null for empty space.</summary>
    (bool Center, SunburstSegment? Segment) HitTest(Point p)
    {
        var (c, hole, ring) = RingGeometry();
        double dx = p.X - c.X, dy = p.Y - c.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < hole)
            return (true, null);
        int r = (int)((dist - hole) / ring) + 1;
        if (r > Rings)
            return (false, null);
        double angle = Math.Atan2(dx, -dy) * 180 / Math.PI;
        if (angle < 0)
            angle += 360;
        return (false, SunburstLayout.HitTest(_segments, r, angle));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (_, seg) = HitTest(e.GetPosition(this));
        if (!Nullable.Equals(seg, _hover))
        {
            _hover = seg;
            SetCurrentValue(HoveredNodeProperty, seg is { IsOther: false } s ? s.Node : -1);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        SetCurrentValue(HoveredNodeProperty, -1);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var tree = Tree;
        if (tree is null)
            return;
        var (center, seg) = HitTest(e.GetPosition(this));
        if (center)
        {
            if (RootNode > 0)
                SetCurrentValue(RootNodeProperty, tree.Parent(RootNode));
        }
        else if (seg is { } s)
        {
            if (s.IsOther)
            {
                SetCurrentValue(SelectedNodeProperty, s.ParentNode);
            }
            else if (e.ClickCount == 2 && tree.IsDirectory(s.Node) && tree.ChildCount(s.Node) > 0)
            {
                SetCurrentValue(RootNodeProperty, s.Node);
            }
            else
            {
                SetCurrentValue(SelectedNodeProperty, s.Node);
            }
        }
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        var (_, seg) = HitTest(e.GetPosition(this));
        if (seg is { IsOther: false } s)
            SetCurrentValue(SelectedNodeProperty, s.Node);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Back or Key.Escape && Tree is { } tree && RootNode > 0)
        {
            SetCurrentValue(RootNodeProperty, tree.Parent(RootNode));
            e.Handled = true;
        }
    }
}
