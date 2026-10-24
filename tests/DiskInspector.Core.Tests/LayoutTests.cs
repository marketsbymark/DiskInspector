using DiskInspector.Core.Layout;
using DiskInspector.Core.Model;

namespace DiskInspector.Core.Tests;

public class LayoutTests
{
    static ScanTree RandomTree(int seed, int files)
    {
        var rng = new Random(seed);
        var t = new ScanTree(@"C:\");
        var dirs = new List<int> { 0 };
        for (int i = 0; i < files; i++)
        {
            int parent = dirs[rng.Next(dirs.Count)];
            if (rng.NextDouble() < 0.15)
                dirs.Add(t.Add(parent, $"d{i}", NodeFlags.Directory, 0, 0, 0));
            else
            {
                long size = (long)Math.Pow(10, rng.NextDouble() * 7);
                t.Add(parent, $"f{i}.bin", NodeFlags.None, size, size + 100, 0);
            }
        }
        t.Complete();
        return t;
    }

    [Fact]
    public void Squarify_fills_the_rectangle_with_proportional_areas()
    {
        double[] sizes = [600, 300, 200, 100, 50, 25, 10, 5, 1];
        var output = new List<(double X, double Y, double W, double H)>();
        SquarifiedTreemap.Squarify(sizes, 0, 0, 640, 480, output);

        Assert.Equal(sizes.Length, output.Count);
        double total = sizes.Sum();
        for (int i = 0; i < sizes.Length; i++)
        {
            var r = output[i];
            Assert.InRange(r.X, -1e-6, 640 + 1e-6);
            Assert.InRange(r.Y, -1e-6, 480 + 1e-6);
            Assert.True(r.X + r.W <= 640 + 1e-6 && r.Y + r.H <= 480 + 1e-6);
            Assert.Equal(sizes[i] / total * 640 * 480, r.W * r.H, 0.01 * 640 * 480 / sizes.Length);
        }
        Assert.Equal(640 * 480, output.Sum(r => r.W * r.H), 1.0);
        AssertNoOverlap(output);
    }

    static void AssertNoOverlap(IReadOnlyList<(double X, double Y, double W, double H)> rects)
    {
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i];
                var b = rects[j];
                double ox = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
                double oy = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);
                Assert.False(ox > 1e-6 && oy > 1e-6, $"rects {i} and {j} overlap");
            }
    }

    [Fact]
    public void Squarify_keeps_aspect_ratios_reasonable()
    {
        double[] sizes = [6, 6, 4, 3, 2, 2, 1];
        var output = new List<(double X, double Y, double W, double H)>();
        SquarifiedTreemap.Squarify(sizes, 0, 0, 600, 400, output);
        foreach (var r in output)
            Assert.True(Math.Max(r.W / r.H, r.H / r.W) < 4, $"bad aspect {r.W}x{r.H}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Treemap_leaves_cover_root_and_stay_in_bounds(int seed)
    {
        var t = RandomTree(seed, 3000);
        foreach (var metric in new[] { SizeMetric.Logical, SizeMetric.Allocated })
        {
            var layout = SquarifiedTreemap.Compute(t, 0, 800, 600, metric, new TreemapOptions(MinSubdivideSize: 0.0001));
            var leaves = layout.Rects.ToArray().Where(r => r.IsLeaf).ToArray();
            Assert.NotEmpty(leaves);
            foreach (var r in leaves)
            {
                Assert.True(r.X >= -0.01 && r.Y >= -0.01 && r.X + r.W <= 800.01 && r.Y + r.H <= 600.01);
                double expected = (double)t.GetSize(r.Node, metric) / t.GetSize(0, metric) * 800 * 600;
                Assert.Equal(expected, (double)r.W * r.H, Math.Max(0.5, expected * 0.001));
            }
            Assert.Equal(800 * 600, leaves.Sum(r => (double)r.W * r.H), 800 * 600 * 0.001);
        }
    }

    [Fact]
    public void Treemap_hit_test_finds_leaves()
    {
        var t = ModelTests.Sample();
        var layout = SquarifiedTreemap.Compute(t, 0, 400, 300, SizeMetric.Logical);
        int movie = t.FindByPath(@"C:\Users\me\movie.MP4");
        Assert.True(layout.TryGetRect(movie, out var r));
        Assert.Equal(movie, layout.HitTest(r.X + r.W / 2, r.Y + r.H / 2));
        Assert.Equal(-1, layout.HitTest(-5, 10));
        Assert.Equal(-1, layout.HitTest(400, 10));
    }

    [Fact]
    public void Treemap_handles_empty_and_tiny_inputs()
    {
        var empty = new ScanTree(@"C:\");
        empty.Complete();
        Assert.Equal(0, SquarifiedTreemap.Compute(empty, 0, 100, 100, SizeMetric.Logical).Count);
        var t = ModelTests.Sample();
        Assert.Equal(0, SquarifiedTreemap.Compute(t, 0, 0, 100, SizeMetric.Logical).Count);
        var oneByOne = SquarifiedTreemap.Compute(t, 0, 1, 1, SizeMetric.Logical);
        Assert.Equal(1, oneByOne.Count);
    }

    [Fact]
    public void Deep_single_child_chain_does_not_overflow()
    {
        var t = new ScanTree(@"C:\");
        int p = 0;
        for (int i = 0; i < 20_000; i++)
            p = t.Add(p, "d", NodeFlags.Directory, 0, 0, 0);
        t.Add(p, "f.txt", NodeFlags.None, 100, 100, 0);
        t.Complete();
        var layout = SquarifiedTreemap.Compute(t, 0, 200, 200, SizeMetric.Logical);
        Assert.Equal(20_002, layout.Count);
        var seg = SunburstLayout.Compute(t, 0, SizeMetric.Logical, maxRings: 6);
        Assert.Equal(6, seg.Count);
    }

    [Fact]
    public void Renderer_writes_every_pixel_of_a_full_layout()
    {
        var t = ModelTests.Sample();
        var layout = SquarifiedTreemap.Compute(t, 0, 120, 80, SizeMetric.Logical);
        var px = new int[120 * 80];
        TreemapRenderer.Render(layout, 120, 80, _ => 0xFF0000, px, background: 0x000000);
        Assert.All(px, p => Assert.NotEqual(unchecked((int)0xFF000000), p));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void Sunburst_child_sweeps_add_up_to_parent(int seed)
    {
        var t = RandomTree(seed, 4000);
        var segs = SunburstLayout.Compute(t, 0, SizeMetric.Logical, maxRings: 5, minSweepDegrees: 1);
        Assert.Equal(360, segs.Where(s => s.Ring == 1).Sum(s => s.Sweep), 1e-6);
        foreach (var parent in segs.Where(s => !s.IsOther && t.IsDirectory(s.Node) && s.Ring < 5))
        {
            var kids = segs.Where(s => s.ParentNode == parent.Node && s.Ring == parent.Ring + 1).ToList();
            if (kids.Count == 0)
                continue;
            Assert.Equal(parent.Sweep, kids.Sum(k => k.Sweep), 1e-6);
            Assert.All(kids, k => Assert.True(k.StartAngle >= parent.StartAngle - 1e-6 && k.StartAngle + k.Sweep <= parent.StartAngle + parent.Sweep + 1e-6));
        }
        Assert.All(segs.Where(s => !s.IsOther), s => Assert.True(s.Sweep >= 1));
        Assert.True(segs.Max(s => s.Ring) <= 5);
    }

    [Fact]
    public void Sunburst_hit_test()
    {
        var t = ModelTests.Sample();
        var segs = SunburstLayout.Compute(t, 0, SizeMetric.Logical);
        var users = t.FindByPath(@"C:\Users");
        var hit = SunburstLayout.HitTest(segs, 1, 1);
        Assert.NotNull(hit);
        Assert.Equal(users, hit!.Value.Node);
        Assert.Null(SunburstLayout.HitTest(segs, 9, 1));
    }
}
