using System.Globalization;
using System.Text.Json;
using DiskInspector.Core.Model;

namespace DiskInspector.Core.Analysis;

/// <summary>A group of file types. <see cref="Color"/> is 0xRRGGBB.</summary>
public sealed record Category(byte Id, string Name, uint Color);

/// <summary>Maps file extensions to categories, from the built-in list plus an optional user override file.</summary>
public sealed class Categorizer
{
    public const uint FolderColor = 0x8A8A8A;

    readonly Dictionary<string, byte> _byExtension;

    Categorizer(List<Category> categories, Dictionary<string, byte> byExtension)
    {
        Categories = categories;
        _byExtension = byExtension;
    }

    public IReadOnlyList<Category> Categories { get; }

    /// <summary>The catch-all category, always the last one.</summary>
    public Category Other => Categories[^1];

    public static string UserOverridePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskInspector", "categories.json");

    static readonly Lazy<Categorizer> BuiltIn = new(() => Load(includeUserOverride: false));

    public static Categorizer Default => BuiltIn.Value;

    /// <summary>Loads the built-in list and, when asked, merges the user file. A broken user file is ignored.</summary>
    public static Categorizer Load(bool includeUserOverride, string? overridePath = null)
    {
        using var stream = typeof(Categorizer).Assembly.GetManifestResourceStream("DiskInspector.Core.categories.json")
            ?? throw new InvalidOperationException("Built-in categories are missing.");
        var file = JsonSerializer.Deserialize<CategoryFile>(stream, JsonOptions) ?? throw new InvalidDataException("Bad categories file.");

        CategoryFile? user = null;
        string path = overridePath ?? UserOverridePath;
        if (includeUserOverride && File.Exists(path))
        {
            try
            {
                user = JsonSerializer.Deserialize<CategoryFile>(File.ReadAllText(path), JsonOptions);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                LoadError = $"{path}: {e.Message}";
            }
        }
        return Build(file, user);
    }

    /// <summary>Why the user override file was ignored, if it was.</summary>
    public static string? LoadError { get; private set; }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static Categorizer Build(CategoryFile builtIn, CategoryFile? user)
    {
        var defs = new List<CategoryDef>(builtIn.Categories);
        if (user is not null)
        {
            foreach (var u in user.Categories)
            {
                int i = defs.FindIndex(d => string.Equals(d.Name, u.Name, StringComparison.OrdinalIgnoreCase));
                if (i >= 0)
                    defs[i] = new CategoryDef { Name = defs[i].Name, Color = u.Color ?? defs[i].Color, Extensions = [.. defs[i].Extensions, .. u.Extensions] };
                else if (defs.Count < 60 && !string.IsNullOrWhiteSpace(u.Name))
                    defs.Add(u);
            }
        }

        var categories = new List<Category>();
        var map = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in defs)
        {
            byte id = (byte)categories.Count;
            categories.Add(new Category(id, d.Name, ParseColor(d.Color, 0xBAB0AC)));
            foreach (var ext in d.Extensions)
                map[ext.TrimStart('.')] = id;
        }
        // User extensions win over built-in ones.
        if (user is not null)
            foreach (var u in user.Categories)
            {
                byte id = (byte)categories.FindIndex(c => string.Equals(c.Name, u.Name, StringComparison.OrdinalIgnoreCase));
                if (id != 255)
                    foreach (var ext in u.Extensions)
                        map[ext.TrimStart('.')] = id;
            }

        var other = builtIn.Other ?? new CategoryDef { Name = "Other", Color = "#BAB0AC" };
        categories.Add(new Category((byte)categories.Count, other.Name, ParseColor(other.Color, 0xBAB0AC)));
        return new Categorizer(categories, map);
    }

    static uint ParseColor(string? hex, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return fallback;
        var s = hex.Trim().TrimStart('#');
        return s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v) ? v : fallback;
    }

    public Category ForExtension(string extension) =>
        _byExtension.TryGetValue(extension, out byte id) ? Categories[id] : Other;

    /// <summary>Category id for each extension id in <paramref name="table"/>.</summary>
    public byte[] BuildExtensionMap(ExtensionTable table)
    {
        var map = new byte[table.Count];
        for (int i = 0; i < map.Length; i++)
            map[i] = ForExtension(table[(ushort)i]).Id;
        return map;
    }

    sealed class CategoryFile
    {
        public List<CategoryDef> Categories { get; set; } = [];
        public CategoryDef? Other { get; set; }
    }

    sealed class CategoryDef
    {
        public string Name { get; set; } = "";
        public string? Color { get; set; }
        public List<string> Extensions { get; set; } = [];
    }
}
