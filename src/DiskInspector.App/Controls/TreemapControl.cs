using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiskInspector.App.Services;
using DiskInspector.Core.Analysis;
using DiskInspector.Core.Layout;
using DiskInspector.Core.Model;

namespace DiskInspector.App.Controls;

/// <summary>
/// Squarified cushion treemap. Layout and pixel shading run on a background thread; the UI thread
/// only copies the finished bitmap, so the window stays responsive with millions of files.
/// Click selects, double-click zooms into the folder under the mouse, Backspace goes up.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty TreeProperty = DependencyProperty.Register(nameof(Tree), typeof(ScanTree), typeof(TreemapControl),
        new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty RootNodeProperty = DependencyProperty.Register(nameof(RootNode), typeof(int), typeof(TreemapControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnInputChanged));

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(nameof(SelectedNode), typeof(int), typeof(TreemapControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoveredNodeProperty = DependencyProperty.Register(nameof(HoveredNode), typeof(int), typeof(TreemapControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(nameof(Metric), typeof(SizeMetric), typeof(TreemapControl),
        new PropertyMetadata(SizeMetric.Logical, OnInputChanged));

    public static readonly DependencyProperty ColorsProperty = DependencyProperty.Register(nameof(Colors), typeof(CategoryBreakdown), typeof(TreemapControl),
        new PropertyMetadata(null, OnInputChanged));

    public ScanTree? Tree { get => (ScanTree?)GetValue(TreeProperty); set => SetValue(TreeProperty, value); }
    public int RootNode { get => (int)GetValue(RootNodeProperty); set => SetValue(RootNodeProperty, value); }
    public int SelectedNode { get => (int)GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }
    public int HoveredNode { get => (int)GetValue(HoveredNodeProperty); set => SetValue(HoveredNodeProperty, value); }
    public SizeMetric Metric { get => (SizeMetric)GetValue(MetricProperty); set => SetValue(MetricProperty, value); }
    public CategoryBreakdown? Colors { get => (CategoryBreakdown?)GetValue(ColorsProperty); set => SetValue(ColorsProperty, value); }

    readonly DispatcherTimer _resizeTimer;
    WriteableBitmap? _bitmap;
    TreemapLayout? _layout;
    CancellationTokenSource? _renderCts;
    int _version;
    bool _rendering;

    public TreemapControl()
    {
        Focusable = true;
        ClipToBounds = true;
        FocusVisualStyle = null;
        _resizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            StartRender();
        };
        SizeChanged += (_, _) =>
        {
            if (_bitmap is null)
                StartRender();
            else
            {
                _resizeTimer.Stop();
                _resizeTimer.Start();
            }
        };
        Unloaded += (_, _) => _renderCts?.Cancel();
    }

    static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TreemapControl)d).StartRender();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        StartRender();
    }

    void StartRender()
    {
        _renderCts?.Cancel();
        var tree = Tree;
        var colors = Colors;
        if (tree is null || !tree.IsComplete || colors is null || ActualWidth < 4 || ActualHeight < 4)
        {
            _bitmap = null;
            _layout = null;
            InvalidateVisual();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        int ph = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));
        int root = RootNode is >= 0 and var r && r < tree.Count && tree.IsDirectory(r) ? r : ScanTree.RootId;
        var metric = Metric;
        int version = ++_version;
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        _rendering = true;
        InvalidateVisual();

        Task.Run(() =>
        {
            var layout = SquarifiedTreemap.Compute(tree, root, pw, ph, metric, ct: cts.Token);
            var pixels = new int[pw * ph];
            TreemapRenderer.Render(layout, pw, ph, colors.ColorOf, pixels);
            return (layout, pixels);
        }, cts.Token).ContinueWith(t =>
        {
            if (version != _version)
                return;
            _rendering = false;
            if (t.IsCanceled)
                return;
            if (t.IsFaulted)
            {
                Log.Error("Treemap render failed", t.Exception);
                InvalidateVisual();
                return;
            }
            var (layout, pixels) = t.Result;
            var bmp = new WriteableBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, pw, ph), pixels, pw * 4, 0);
            bmp.Freeze();
            _bitmap = bmp;
            _layout = layout;
            SetCurrentValue(HoveredNodeProperty, IsMouseOver ? HitTest(Mouse.GetPosition(this)) : -1);
            InvalidateVisual();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    protected override void OnRender(DrawingContext dc)
    {
        var area = new Rect(RenderSize);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, area);
        if (_bitmap is not null)
            dc.DrawImage(_bitmap, area);

        if (_layout is not null && _bitmap is not null)
        {
            if (HoveredNode >= 0 && FindDrawnRect(HoveredNode, out var hr))
                dc.DrawRectangle(null, HoverPen, Inflate(hr, -0.5));
            if (SelectedNode >= 0 && SelectedNode != _layout.Root && FindDrawnRect(SelectedNode, out var sr))
            {
                dc.DrawRectangle(null, SelectOuterPen, Inflate(sr, -1));
                dc.DrawRectangle(null, SelectInnerPen, Inflate(sr, -1));
            }
        }

        if (_rendering && _bitmap is null)
        {
            var ft = new System.Windows.Media.FormattedText("Drawing the map…", System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, Brushes.Gainsboro, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, (ActualHeight - ft.Height) / 2));
        }
    }

    static Rect Inflate(Rect r, double d)
    {
        var x = r;
        if (x.Width > -2 * d && x.Height > -2 * d)
            x.Inflate(d, d);
        return x;
    }

    static readonly Pen HoverPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1));
    static readonly Pen SelectOuterPen = Frozen(new Pen(Brushes.Black, 4));
    static readonly Pen SelectInnerPen = Frozen(new Pen(Brushes.Yellow, 2));

    static Pen Frozen(Pen p)
    {
        p.Freeze();
        return p;
    }

    /// <summary>Rectangle (in DIPs) for a node, or for its nearest ancestor that was drawn.</summary>
    bool FindDrawnRect(int node, out Rect rect)
    {
        rect = default;
        var tree = Tree;
        if (_layout is null || tree is null || node >= tree.Count)
            return false;
        for (int n = node; n >= 0; n = tree.Parent(n))
        {
            if (_layout.TryGetRect(n, out var r))
            {
                double sx = ActualWidth / _layout.Width, sy = ActualHeight / _layout.Height;
                rect = new Rect(r.X * sx, r.Y * sy, Math.Max(1, r.W * sx), Math.Max(1, r.H * sy));
                return true;
            }
            if (n == _layout.Root)
                break;
        }
        return false;
    }

    int HitTest(Point p)
    {
        if (_layout is null || ActualWidth <= 0 || ActualHeight <= 0)
            return -1;
        return _layout.HitTest(p.X / ActualWidth * _layout.Width, p.Y / ActualHeight * _layout.Height);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitTest(e.GetPosition(this));
        if (hit != HoveredNode)
        {
            SetCurrentValue(HoveredNodeProperty, hit);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetCurrentValue(HoveredNodeProperty, -1);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        int hit = HitTest(e.GetPosition(this));
        if (hit < 0)
            return;
        if (e.ClickCount == 2)
            ZoomToward(hit);
        else
            SetCurrentValue(SelectedNodeProperty, hit);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        int hit = HitTest(e.GetPosition(this));
        if (hit >= 0)
            SetCurrentValue(SelectedNodeProperty, hit);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Back or Key.Escape && Tree is { } tree && RootNode > 0)
        {
            SetCurrentValue(RootNodeProperty, tree.Parent(RootNode));
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SelectedNode >= 0)
        {
            ZoomToward(SelectedNode);
            e.Handled = true;
        }
    }

    /// <summary>Zooms one level: into the child of the current root that contains <paramref name="node"/>.</summary>
    void ZoomToward(int node)
    {
        var tree = Tree;
        if (tree is null)
            return;
        int root = RootNode;
        int child = node;
        while (child >= 0 && tree.Parent(child) != root)
            child = tree.Parent(child);
        if (child >= 0 && tree.IsDirectory(child) && tree.ChildCount(child) > 0)
            SetCurrentValue(RootNodeProperty, child);
    }
}
