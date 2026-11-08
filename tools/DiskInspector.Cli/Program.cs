using System.Diagnostics;
using DiskInspector.Core.Analysis;
using DiskInspector.Core.Layout;
using DiskInspector.Core.Model;
using DiskInspector.Core.Scanning;

// Usage: DiskInspector.Cli <path> [--fast] [--compare <folder>]
// Scans a drive or folder and prints timing, memory and totals. Used to verify the scanners on real disks.
if (args.Length == 0)
{
    Console.WriteLine("Usage: DiskInspector.Cli <path> [--fast] [--compare <folder>]");
    return 1;
}

if (args[0] == "--authors" && args.Length > 1)
{
    // DiskInspector.Cli --authors <folder> [max]: prints what the document reader finds, tab-separated.
    int max = args.Length > 2 && int.TryParse(args[2], out int m) ? m : 200;
    int shown = 0, withAuthor = 0;
    var clock = Stopwatch.StartNew();
    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Offline | (FileAttributes)0x400000 };
    foreach (var file in Directory.EnumerateFiles(args[1], "*", options))
    {
        if (!DocumentProperties.CanRead(Path.GetExtension(file).TrimStart('.')))
            continue;
        var info = DocumentProperties.TryRead(file);
        if (info?.Author is not null)
            withAuthor++;
        Console.WriteLine($"{file}\t{(info is null ? "(unreadable)" : info.Author)}\t{info?.LastSavedBy}\t{info?.Title}");
        if (++shown >= max)
            break;
    }
    Console.Error.WriteLine($"{shown} files, {withAuthor} with an author, {clock.ElapsedMilliseconds} ms");
    return 0;
}

string target = args[0];
bool fast = args.Contains("--fast");
int ci = Array.IndexOf(args, "--compare");
string? compare = ci >= 0 && ci + 1 < args.Length ? args[ci + 1] : null;

IScanner scanner = fast ? new MftScanner() : new StandardScanner();
var progress = new Progress<ScanProgress>(p => Console.Error.Write($"\r{p.Files,12:N0} files {Format.Bytes(p.Bytes),12}  {p.Elapsed.TotalSeconds,6:0.0}s   "));
long before = GC.GetTotalMemory(true);
var result = await scanner.ScanAsync(target, progress);
Console.Error.WriteLine();
var t = result.Tree;
long after = GC.GetTotalMemory(true);

Console.WriteLine($"Mode:          {result.Mode}");
Console.WriteLine($"Root:          {t.RootPath}");
Console.WriteLine($"Duration:      {Format.Duration(result.Duration)}");
Console.WriteLine($"Nodes:         {t.Count:N0} ({t.FileCount(0):N0} files, {t.DirectoryCount(0):N0} folders)");
Console.WriteLine($"Size:          {Format.Bytes(t.Size(0))} ({t.Size(0):N0} bytes)");
Console.WriteLine($"Size on disk:  {Format.Bytes(t.Allocated(0))}");
if (result.Volume is { } v)
    Console.WriteLine($"Volume used:   {Format.Bytes(v.UsedBytes)} of {Format.Bytes(v.TotalBytes)} ({v.Format}, cluster {v.ClusterSize})");
Console.WriteLine($"Issues:        {result.Issues.Count:N0}");
foreach (var i in result.Issues.Take(5))
    Console.WriteLine($"   {i.Path}: {i.Message}");
Console.WriteLine($"Managed heap:  {Format.Bytes(after - before)} for the tree; process peak working set {Format.Bytes(Process.GetCurrentProcess().PeakWorkingSet64)}");

var sw = Stopwatch.StartNew();
var cats = Categorizer.Default;
var ext = FileTypeAnalysis.ByExtension(t, cats);
var byCat = FileTypeAnalysis.ByCategory(ext, cats);
var breakdown = new CategoryBreakdown(t, cats);
var locations = LocationAnalysis.Compute(result);
var largest = FileQueries.Largest(t, 1000, SizeMetric.Logical);
var age = AgeAnalysis.Compute(t, DateTime.UtcNow);
Console.WriteLine($"Analysis:      {sw.ElapsedMilliseconds:N0} ms ({ext.Count:N0} extensions)");
sw.Restart();
var layout = SquarifiedTreemap.Compute(t, 0, 2560, 1440, SizeMetric.Logical);
long layoutMs = sw.ElapsedMilliseconds;
var px = new int[2560 * 1440];
TreemapRenderer.Render(layout, 2560, 1440, breakdown.ColorOf, px);
Console.WriteLine($"Treemap:       {layout.Count:N0} rects, layout {layoutMs:N0} ms, render {sw.ElapsedMilliseconds - layoutMs:N0} ms at 2560x1440");
sw.Restart();
var sun = SunburstLayout.Compute(t, 0, SizeMetric.Logical);
Console.WriteLine($"Sunburst:      {sun.Count:N0} segments, {sw.ElapsedMilliseconds:N0} ms");

Console.WriteLine("\nTop folders:");
foreach (int c in t.Children(0).ToArray().Take(10))
    Console.WriteLine($"  {Format.Bytes(t.Size(c)),10}  {t.Name(c)}");
Console.WriteLine("\nCategories:");
foreach (var c in byCat.Where(c => c.Size > 0))
    Console.WriteLine($"  {Format.Bytes(c.Size),10}  {c.Category.Name} ({c.Count:N0} files)");
Console.WriteLine("\nLargest uncategorized extensions:");
foreach (var e in ext.Where(e => e.Category == cats.Other).Take(25))
    Console.WriteLine($"  {Format.Bytes(e.Size),10}  {e.DisplayName} ({e.Count:N0} files, e.g. {t.GetFullPath(e.LargestNode)})");
Console.WriteLine("\nLargest files:");
foreach (int f in largest.Take(10))
    Console.WriteLine($"  {Format.Bytes(t.Size(f)),10}  {t.GetFullPath(f)}");
Console.WriteLine("\nLocations:");
foreach (var l in locations.Where(l => l.Group != "Top-level folders").Take(30))
    Console.WriteLine($"  {Format.Bytes(l.Size),10}  {l.Group} / {new string(' ', l.Level * 2)}{l.Name}");

if (compare is not null)
{
    var normal = await new StandardScanner().ScanAsync(compare);
    int id = t.FindByPath(ScanPaths.Normalize(compare));
    Console.WriteLine($"\nCompare {compare}:");
    Console.WriteLine($"  this scan: {(id < 0 ? "not found" : $"{t.Size(id):N0} bytes, {t.FileCount(id):N0} files")}");
    Console.WriteLine($"  standard:  {normal.Tree.Size(0):N0} bytes, {normal.Tree.FileCount(0):N0} files");
    if (id >= 0)
    {
        // Where do the two scans disagree? Walk down the biggest differences.
        void Diff(int fastNode, int stdNode, int depth)
        {
            foreach (int c in normal.Tree.Children(stdNode).ToArray())
            {
                int f = t.FindChild(fastNode, normal.Tree.Name(c));
                long a = normal.Tree.Size(c), b = f >= 0 ? t.Size(f) : 0;
                if (Math.Abs(a - b) < 50L * 1024 * 1024)
                    continue;
                int links = 0;
                if (f >= 0)
                    for (int i = 0; i < t.Count; i++)
                        if ((t.Flags(i) & NodeFlags.HardLink) != 0 && t.IsAncestorOrSelf(f, i) && t.Size(i) == 0)
                            links++;
                Console.WriteLine($"  {new string(' ', depth * 2)}{normal.Tree.Name(c)}: standard {Format.Bytes(a)}, this scan {Format.Bytes(b)}, zero-size hard links here {links:N0}");
                if (f >= 0 && depth < 3 && normal.Tree.IsDirectory(c))
                    Diff(f, c, depth + 1);
            }
        }
        Diff(id, 0, 1);
    }
}
return 0;
