using DiskInspector.Core.Model;

namespace DiskInspector.Core.Layout;

/// <summary>
/// One slice of a sunburst ring. Angles are in degrees, clockwise from 12 o'clock.
/// <see cref="Node"/> is -1 for an "other" slice that merges children too small to show.
/// </summary>
public readonly record struct SunburstSegment(int Node, int ParentNode, int Ring, double StartAngle, double Sweep, long Size, int OtherCount)
{
    public bool IsOther => Node < 0;
}

public static class SunburstLayout
{
    public static IReadOnlyList<SunburstSegment> Compute(ScanTree tree, int root, SizeMetric metric, int maxRings = 6,
        double minSweepDegrees = 0.5, CancellationToken ct = default)
    {
        var result = new List<SunburstSegment>();
        if (tree.GetSize(root, metric) <= 0)
            return result;

        var stack = new Stack<(int Node, int Ring, double Start, double Sweep)>();
        stack.Push((root, 0, 0, 360));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (node, ring, start, sweep) = stack.Pop();
            if (ring >= maxRings || !tree.IsDirectory(node))
                continue;
            long total = tree.GetSize(node, metric);
            if (total <= 0)
                continue;

            double angle = start;
            long otherSize = 0;
            int otherCount = 0;
            var children = metric == SizeMetric.Logical ? tree.Children(node).ToArray() : tree.GetChildrenSorted(node, metric);
            foreach (int c in children)
            {
                long s = tree.GetSize(c, metric);
                if (s <= 0)
                    continue;
                double sw = sweep * s / total;
                if (sw < minSweepDegrees)
                {
                    otherSize += s;
                    otherCount++;
                    continue;
                }
                result.Add(new SunburstSegment(c, node, ring + 1, angle, sw, s, 0));
                if (tree.IsDirectory(c))
                    stack.Push((c, ring + 1, angle, sw));
                angle += sw;
            }
            if (otherSize > 0)
                result.Add(new SunburstSegment(-1, node, ring + 1, angle, sweep * otherSize / total, otherSize, otherCount));
        }
        result.Sort((a, b) => a.Ring != b.Ring ? a.Ring.CompareTo(b.Ring) : a.StartAngle.CompareTo(b.StartAngle));
        return result;
    }

    /// <summary>Segment under a polar position (ring index, angle in degrees), or null.</summary>
    public static SunburstSegment? HitTest(IReadOnlyList<SunburstSegment> segments, int ring, double angle)
    {
        foreach (var s in segments)
            if (s.Ring == ring && angle >= s.StartAngle && angle < s.StartAngle + s.Sweep)
                return s;
        return null;
    }
}
