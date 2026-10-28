using System.Text.Json;

namespace CouchHud.Core.Plugins;

/// <summary>
/// The names of the .NET shared framework assemblies this process runs on (the runtime and, for the shell, the Windows desktop pack): the
/// only ones a plugin that is not CouchHUD's own may get from the default load context. Anything else the host ships (WebView2, QRCoder, the
/// service controller package, ...) is the host's, and a plugin that needs it brings its own copy.
/// Framework-dependent: the trusted platform assemblies outside the app folder. Self-contained (the setup): the runtime packs in the app's
/// .deps.json. Nothing found (an unusual host): an empty set, so nothing but the SDK falls through.
/// </summary>
internal static class SharedFramework
{
    private static readonly Lazy<IReadOnlySet<string>> Names = new(Read);

    public static bool Contains(string? assembly) => assembly is not null && Names.Value.Contains(assembly);

    private static IReadOnlySet<string> Read()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var app = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
            {
                foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!Path.GetFullPath(path).StartsWith(app, StringComparison.OrdinalIgnoreCase)) names.Add(Path.GetFileNameWithoutExtension(path));
                }
            }
            if (AppContext.GetData("APP_CONTEXT_DEPS_FILES") is string deps)
            {
                foreach (var file in deps.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) AddRuntimePacks(file, names);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        {
            Console.Error.WriteLine($"[plugins] shared framework not read: {ex.Message}");
        }
        return names;
    }

    /// <summary>The runtime assets of the libraries of type "runtimepack" (what a self-contained app carries of the framework).</summary>
    internal static void AddRuntimePacks(string depsFile, HashSet<string> names)
    {
        if (!File.Exists(depsFile)) return;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(depsFile));
        var root = doc.RootElement;
        if (!root.TryGetProperty("libraries", out var libraries) || !root.TryGetProperty("targets", out var targets)) return;
        foreach (var target in targets.EnumerateObject())
        {
            if (target.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var library in target.Value.EnumerateObject())
            {
                if (!libraries.TryGetProperty(library.Name, out var info) || !info.TryGetProperty("type", out var type) || type.GetString() != "runtimepack") continue;
                if (!library.Value.TryGetProperty("runtime", out var runtime) || runtime.ValueKind != JsonValueKind.Object) continue;
                foreach (var asset in runtime.EnumerateObject())
                {
                    if (asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) names.Add(Path.GetFileNameWithoutExtension(asset.Name));
                }
            }
        }
    }
}
