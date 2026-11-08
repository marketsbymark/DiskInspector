using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace DiskInspector.App.Controls;

public static class BrushCache
{
    static readonly Dictionary<uint, SolidColorBrush> Cache = [];
    static readonly object Gate = new();

    public static Color ToColor(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Frozen brush for a 0xRRGGBB color; safe to use from any thread.</summary>
    public static SolidColorBrush Get(uint rgb)
    {
        lock (Gate)
        {
            if (!Cache.TryGetValue(rgb, out var b))
            {
                b = new SolidColorBrush(ToColor(rgb));
                b.Freeze();
                Cache[rgb] = b;
            }
            return b;
        }
    }

    public static Brush Theme(FrameworkElement e, string key, Brush fallback) =>
        e.TryFindResource(key) as Brush ?? fallback;
}

/// <summary>ObservableCollection that can swap in many items with a single Reset notification.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var i in items)
            Items.Add(i);
        RaiseReset();
    }

    public void InsertRange(int index, IEnumerable<T> items)
    {
        foreach (var i in items)
            Items.Insert(index++, i);
        RaiseReset();
    }

    public void RemoveRange(int index, int count)
    {
        for (int k = 0; k < count; k++)
            Items.RemoveAt(index);
        RaiseReset();
    }

    void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>A thin horizontal bar showing a 0..1 share. Much lighter than a ProgressBar in long lists.</summary>
public sealed class ProportionBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ProportionBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(ProportionBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, 8);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;
        var track = BrushCache.Theme(this, "ControlStrongFillColorDisabledBrush", new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)));
        var fill = Fill ?? BrushCache.Theme(this, "AccentFillColorDefaultBrush", Brushes.SteelBlue);
        double v = double.IsNaN(Value) ? 0 : Math.Clamp(Value, 0, 1);
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), 2, 2);
        if (v > 0)
            dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, Math.Max(2, w * v), h), 2, 2);
    }
}

public sealed record DonutSlice(string Label, double Value, Brush Fill);

/// <summary>Simple donut chart with a caption in the middle.</summary>
public sealed class DonutChart : FrameworkElement
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(nameof(Slices), typeof(IEnumerable), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterTextProperty = DependencyProperty.Register(nameof(CenterText), typeof(string), typeof(DonutChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterSubTextProperty = DependencyProperty.Register(nameof(CenterSubText), typeof(string), typeof(DonutChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Slices { get => (IEnumerable?)GetValue(SlicesProperty); set => SetValue(SlicesProperty, value); }
    public string CenterText { get => (string)GetValue(CenterTextProperty); set => SetValue(CenterTextProperty, value); }
    public string CenterSubText { get => (string)GetValue(CenterSubTextProperty); set => SetValue(CenterSubTextProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 20)
            return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double outer = size / 2 - 2, inner = outer * 0.62;
        var slices = Slices?.OfType<DonutSlice>().Where(s => s.Value > 0).ToList() ?? [];
        double total = slices.Sum(s => s.Value);
        var bg = BrushCache.Theme(this, "SolidBackgroundFillColorBaseBrush", Brushes.White);
        var pen = new Pen(bg, 1.5);
        pen.Freeze();

        if (total <= 0)
        {
            dc.DrawEllipse(null, new Pen(Brushes.Gray, outer - inner), center, (outer + inner) / 2, (outer + inner) / 2);
        }
        else
        {
            double angle = 0;
            foreach (var s in slices)
            {
                double sweep = 360 * s.Value / total;
                dc.DrawGeometry(s.Fill, pen, ArcGeometry.Segment(center, inner, outer, angle, sweep));
                angle += sweep;
            }
        }

        var fg = BrushCache.Theme(this, "TextFillColorPrimaryBrush", Brushes.Black);
        var fg2 = BrushCache.Theme(this, "TextFillColorSecondaryBrush", Brushes.Gray);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var t1 = new FormattedText(CenterText ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, Math.Max(10, inner * 0.28), fg, dpi)
        { MaxTextWidth = inner * 1.7, TextAlignment = TextAlignment.Center, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        var t2 = new FormattedText(CenterSubText ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), Math.Max(9, inner * 0.15), fg2, dpi)
        { MaxTextWidth = inner * 1.6, TextAlignment = TextAlignment.Center, MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis };
        double totalH = t1.Height + t2.Height;
        dc.DrawText(t1, new Point(center.X - t1.MaxTextWidth / 2, center.Y - totalH / 2));
        dc.DrawText(t2, new Point(center.X - t2.MaxTextWidth / 2, center.Y - totalH / 2 + t1.Height));
    }
}

public static class ArcGeometry
{
    /// <summary>Point at a radius and angle (degrees clockwise from 12 o'clock).</summary>
    public static Point At(Point c, double radius, double degrees)
    {
        double a = degrees * Math.PI / 180;
        return new Point(c.X + radius * Math.Sin(a), c.Y - radius * Math.Cos(a));
    }

    /// <summary>A ring segment. Handles full circles, which a single arc cannot draw.</summary>
    public static Geometry Segment(Point c, double inner, double outer, double start, double sweep)
    {
        if (sweep >= 359.99)
        {
            var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
            ring.Children.Add(new EllipseGeometry(c, outer, outer));
            if (inner > 0)
                ring.Children.Add(new EllipseGeometry(c, inner, inner));
            ring.Freeze();
            return ring;
        }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            bool large = sweep > 180;
            ctx.BeginFigure(At(c, outer, start), true, true);
            ctx.ArcTo(At(c, outer, start + sweep), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
            if (inner > 0)
            {
                ctx.LineTo(At(c, inner, start + sweep), true, false);
                ctx.ArcTo(At(c, inner, start), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
            }
            else
            {
                ctx.LineTo(c, true, false);
            }
        }
        g.Freeze();
        return g;
    }
}

/// <summary>Click a GridView column header to sort by the property named in <c>GridViewSort.Property</c>.</summary>
public static class GridViewSort
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(GridViewSort),
        new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty PropertyProperty = DependencyProperty.RegisterAttached("Property", typeof(string), typeof(GridViewSort),
        new PropertyMetadata(null));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
    public static string? GetProperty(DependencyObject o) => (string?)o.GetValue(PropertyProperty);
    public static void SetProperty(DependencyObject o, string? v) => o.SetValue(PropertyProperty, v);

    sealed class SortState
    {
        public string? Property;
        public ListSortDirection Direction;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ListView, SortState> LastSort = new();

    static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ListView lv && (bool)e.NewValue)
            lv.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
    }

    static void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ListView lv || e.OriginalSource is not GridViewColumnHeader { Column: { } column } || lv.ItemsSource is null)
            return;
        string? prop = GetProperty(column);
        if (string.IsNullOrEmpty(prop))
            return;
        if (lv.DataContext is ViewModels.ISortableTable table)
        {
            // Large tables sort in the view model, which also keeps snapshots in the order shown.
            var last = LastSort.GetOrCreateValue(lv);
            var direction = last.Property == prop && last.Direction == ListSortDirection.Descending ? ListSortDirection.Ascending : ListSortDirection.Descending;
            last.Property = prop;
            last.Direction = direction;
            table.SortBy(prop, direction);
            return;
        }
        var view = CollectionViewSource.GetDefaultView(lv.ItemsSource);
        var dir = ListSortDirection.Descending;
        if (view.SortDescriptions.Count > 0 && view.SortDescriptions[0].PropertyName == prop)
            dir = view.SortDescriptions[0].Direction == ListSortDirection.Descending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(prop, dir));
        }
    }
}

public sealed class LevelToIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness((value is int i ? i : 0) * 16, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the bound index equals the converter parameter.</summary>
public sealed class IndexToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && int.TryParse(parameter?.ToString(), out int p) && i == p ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the bound value is null or an empty string.</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool empty = value is null || value is string s && s.Length == 0 || value is System.Collections.ICollection c && c.Count == 0;
        return empty ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0xRRGGBB to a frozen brush.</summary>
public sealed class ColorToBrush : IValueConverter
{
    public static ColorToBrush Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is uint rgb ? BrushCache.Get(rgb) : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
