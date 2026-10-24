using DiskInspector.Core.Model;

namespace DiskInspector.Core.Layout;

/// <summary>
/// One rectangle in a treemap, in pixels. S1X..S2Y are the cushion-shading coefficients
/// (van Wijk &amp; van de Wetering) accumulated from the root down to this rectangle.
/// </summary>
public struct TreemapRect
{
    public int Node;
    public float X, Y, W, H;
    public float S1X, S2X, S1Y, S2Y;
    public short Depth;
    public bool IsLeaf;
}

public sealed record TreemapOptions(
    double MinSubdivideSize = 3,
    double CushionHeight = 0.5,
    double CushionFalloff = 0.75,
    int MaxRects = 2_000_000);

public sealed class TreemapLayout
{
    readonly TreemapRect[] _rects;
    readonly int _count;
    Dictionary<int, int>? _byNode;
    int[]? _cellStart;
    int[]? _cellItems;
    const int Grid = 64;

    internal TreemapLayout(TreemapRect[] rects, int count, int width, int height, int root)
    {
        _rects = rects;
        _count = count;
        Width = width;
        Height = height;
        Root = root;
    }

    public int Width { get; }
    public int Height { get; }
    public int Root { get; }
    public int Count => _count;
    public ReadOnlySpan<TreemapRect> Rects => _rects.AsSpan(0, _count);
    internal TreemapRect[] RawRects => _rects;

    public bool TryGetRect(int node, out TreemapRect rect)
    {
        if (_byNode is null)
        {
            var map = new Dictionary<int, int>(_count);
            for (int i = 0; i < _count; i++)
                map[_rects[i].Node] = i;
            _byNode = map;
        }
        if (_byNode.TryGetValue(node, out int idx))
        {
            rect = _rects[idx];
            return true;
        }
        rect = default;
        return false;
    }

    /// <summary>The leaf node under a pixel position, or -1.</summary>
    public int HitTest(double x, double y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return -1;
        EnsureGrid();
        int cx = Math.Min(Grid - 1, (int)(x * Grid / Width));
        int cy = Math.Min(Grid - 1, (int)(y * Grid / Height));
        int cell = cy * Grid + cx;
        for (int k = _cellStart![cell]; k < _cellStart[cell + 1]; k++)
        {
            ref var r = ref _rects[_cellItems![k]];
            if (x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H)
                return r.Node;
        }
        return -1;
    }

    void EnsureGrid()
    {
        if (_cellStart is not null)
            return;
        var counts = new int[Grid * Grid + 1];
        ForEachCell((cell, _) => counts[cell]++);
        var start = new int[Grid * Grid + 1];
        for (int c = 0; c < Grid * Grid; c++)
            start[c + 1] = start[c] + counts[c];
        var items = new int[start[Grid * Grid]];
        var cursor = (int[])start.Clone();
        ForEachCell((cell, i) => items[cursor[cell]++] = i);
        _cellItems = items;
        _cellStart = start;
    }

    void ForEachCell(Action<int, int> action)
    {
        double sx = (double)Grid / Width, sy = (double)Grid / Height;
        for (int i = 0; i < _count; i++)
        {
            ref var r = ref _rects[i];
            if (!r.IsLeaf || r.W <= 0 || r.H <= 0)
                continue;
            int x0 = Math.Clamp((int)(r.X * sx), 0, Grid - 1), x1 = Math.Clamp((int)((r.X + r.W) * sx), 0, Grid - 1);
            int y0 = Math.Clamp((int)(r.Y * sy), 0, Grid - 1), y1 = Math.Clamp((int)((r.Y + r.H) * sy), 0, Grid - 1);
            for (int cy = y0; cy <= y1; cy++)
                for (int cx = x0; cx <= x1; cx++)
                    action(cy * Grid + cx, i);
        }
    }
}

/// <summary>Squarified treemap (Bruls, Huizing &amp; van Wijk) with cushion coefficients.</summary>
public static class SquarifiedTreemap
{
    public static TreemapLayout Compute(ScanTree tree, int root, int width, int height, SizeMetric metric,
        TreemapOptions? options = null, CancellationToken ct = default)
    {
        options ??= new TreemapOptions();
        var rects = new List<TreemapRect>(4096);
        if (width <= 0 || height <= 0 || tree.GetSize(root, metric) <= 0)
            return new TreemapLayout([], 0, Math.Max(width, 1), Math.Max(height, 1), root);

        var stack = new Stack<Work>();
        stack.Push(new Work(root, 0, 0, width, height, 0, 0, 0, 0, 0));
        var sizes = new List<double>();
        var ids = new List<int>();
        var placed = new List<(double X, double Y, double W, double H)>();

        while (stack.Count > 0)
        {
            if ((rects.Count & 0x3FFF) == 0)
                ct.ThrowIfCancellationRequested();

            var w = stack.Pop();
            double h = options.CushionHeight * Math.Pow(options.CushionFalloff, w.Depth);
            double s1x = w.S1X, s2x = w.S2X, s1y = w.S1Y, s2y = w.S2Y;
            AddRidge(w.X, w.X + w.W, h, ref s1x, ref s2x);
            AddRidge(w.Y, w.Y + w.H, h, ref s1y, ref s2y);

            ids.Clear();
            sizes.Clear();
            bool subdivide = tree.IsDirectory(w.Node) && w.W >= options.MinSubdivideSize && w.H >= options.MinSubdivideSize
                && rects.Count < options.MaxRects;
            if (subdivide)
            {
                foreach (int c in metric == SizeMetric.Logical ? tree.Children(w.Node) : tree.GetChildrenSorted(w.Node, metric))
                {
                    long s = tree.GetSize(c, metric);
                    if (s > 0)
                    {
                        ids.Add(c);
                        sizes.Add(s);
                    }
                }
                subdivide = ids.Count > 0;
            }

            rects.Add(new TreemapRect
            {
                Node = w.Node,
                X = (float)w.X, Y = (float)w.Y, W = (float)w.W, H = (float)w.H,
                S1X = (float)s1x, S2X = (float)s2x, S1Y = (float)s1y, S2Y = (float)s2y,
                Depth = (short)Math.Min(w.Depth, short.MaxValue),
                IsLeaf = !subdivide,
            });
            if (!subdivide)
                continue;

            placed.Clear();
            Squarify(sizes, w.X, w.Y, w.W, w.H, placed);
            // Push in reverse so the largest child is laid out (and drawn) first.
            for (int k = placed.Count - 1; k >= 0; k--)
            {
                var p = placed[k];
                if (p.W <= 0 || p.H <= 0)
                    continue;
                stack.Push(new Work(ids[k], p.X, p.Y, p.W, p.H, w.Depth + 1, s1x, s2x, s1y, s2y));
            }
        }

        var arr = rects.ToArray();
        return new TreemapLayout(arr, arr.Length, width, height, root);
    }

    static void AddRidge(double x1, double x2, double h, ref double s1, ref double s2)
    {
        double len = x2 - x1;
        if (len <= 0)
            return;
        s1 += 4 * h * (x2 + x1) / len;
        s2 -= 4 * h / len;
    }

    /// <summary>Lays out sizes (sorted largest first) inside the rectangle, appending one rect per size.</summary>
    internal static void Squarify(IReadOnlyList<double> sizes, double x, double y, double w, double h,
        List<(double X, double Y, double W, double H)> output)
    {
        int n = sizes.Count;
        double total = 0;
        for (int i = 0; i < n; i++)
            total += sizes[i];
        if (total <= 0 || w <= 0 || h <= 0)
        {
            for (int i = 0; i < n; i++)
                output.Add((x, y, 0, 0));
            return;
        }

        double scale = w * h / total;
        int start = 0;
        while (start < n)
        {
            bool vertical = w >= h;           // wide space: the row is a column on the left
            double side = vertical ? h : w;   // length of the row
            double rowSum = 0, best = double.MaxValue;
            int end = start;
            while (end < n)
            {
                double a = sizes[end] * scale;
                double sum = rowSum + a;
                double worst = Worst(sizes[start] * scale, a, sum, side);
                if (end > start && worst > best)
                    break;
                best = worst;
                rowSum = sum;
                end++;
            }

            bool last = end == n;
            double thickness = side > 0 ? rowSum / side : 0;
            if (last)
                thickness = vertical ? w : h;   // absorb rounding so the parent is filled exactly
            double offset = 0;
            for (int k = start; k < end; k++)
            {
                double len = thickness > 0 ? sizes[k] * scale / thickness : 0;
                if (k == end - 1)
                    len = side - offset;
                if (vertical)
                    output.Add((x, y + offset, thickness, Math.Max(0, len)));
                else
                    output.Add((x + offset, y, Math.Max(0, len), thickness));
                offset += len;
            }

            if (vertical)
            {
                x += thickness;
                w = Math.Max(0, w - thickness);
            }
            else
            {
                y += thickness;
                h = Math.Max(0, h - thickness);
            }
            start = end;
        }
    }

    static double Worst(double maxArea, double minArea, double sum, double side)
    {
        if (sum <= 0 || side <= 0 || minArea <= 0)
            return double.MaxValue;
        double s2 = sum * sum, w2 = side * side;
        return Math.Max(w2 * maxArea / s2, s2 / (w2 * minArea));
    }

    readonly record struct Work(int Node, double X, double Y, double W, double H, int Depth,
        double S1X, double S2X, double S1Y, double S2Y);
}

public static class TreemapRenderer
{
    // Light from the upper left, as in WinDirStat.
    const double Lx = -0.09759, Ly = -0.19518, Lz = 0.9759;
    const double Ambient = 0.18, Diffuse = 0.82;

    /// <summary>Renders leaf rectangles with cushion shading into a 32-bit BGRA pixel buffer.</summary>
    public static void Render(TreemapLayout layout, int width, int height, Func<int, uint> colorOf, int[] pixels, uint background = 0x202020)
    {
        Array.Fill(pixels, unchecked((int)(0xFF000000 | background)));
        var rects = layout.RawRects;
        Parallel.For(0, layout.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            ref var r = ref rects[i];
            if (!r.IsLeaf)
                return;
            int x0 = Math.Clamp((int)Math.Round(r.X), 0, width), x1 = Math.Clamp((int)Math.Round(r.X + r.W), 0, width);
            int y0 = Math.Clamp((int)Math.Round(r.Y), 0, height), y1 = Math.Clamp((int)Math.Round(r.Y + r.H), 0, height);
            if (x1 <= x0 || y1 <= y0)
                return;

            uint c = colorOf(r.Node);
            double cr = (c >> 16) & 0xFF, cg = (c >> 8) & 0xFF, cb = c & 0xFF;
            for (int py = y0; py < y1; py++)
            {
                double ny = -(2 * r.S2Y * (py + 0.5) + r.S1Y);
                int row = py * width;
                for (int px = x0; px < x1; px++)
                {
                    double nx = -(2 * r.S2X * (px + 0.5) + r.S1X);
                    double cos = (nx * Lx + ny * Ly + Lz) / Math.Sqrt(nx * nx + ny * ny + 1.0);
                    double light = Ambient + Diffuse * Math.Max(0, cos);
                    light *= 1.12;
                    int R = (int)Math.Min(255, cr * light), G = (int)Math.Min(255, cg * light), B = (int)Math.Min(255, cb * light);
                    pixels[row + px] = unchecked((int)(0xFF000000 | (uint)(R << 16) | (uint)(G << 8) | (uint)B));
                }
            }
        });
    }
}
