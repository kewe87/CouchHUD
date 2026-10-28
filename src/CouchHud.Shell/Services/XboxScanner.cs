using CouchHud.Sdk;
using Windows.Management.Deployment;

namespace CouchHud.Shell.Services;

/// <summary>
/// Xbox app / Game Pass and Microsoft Store games. They are store packages; the ones
/// with a MicrosoftGame.config are games. Started through their app id like the Start menu does.
/// </summary>
public sealed class XboxScanner : ILibraryProvider, CouchHud.Core.Library.IGameStore
{
    public string Name => "Xbox";
    public string Source => "xbox";

    public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken) => Task.Run(ScanAsync, cancellationToken);

    private static async Task<IReadOnlyList<LibraryItem>> ScanAsync()
    {
        var items = new List<LibraryItem>();
        IEnumerable<Windows.ApplicationModel.Package> packages;
        try
        {
            packages = new PackageManager().FindPackagesForUser(string.Empty);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Console.Error.WriteLine($"[xbox] {ex.Message}");
            return items;
        }

        foreach (var package in packages)
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage) continue;
                var dir = package.InstalledLocation?.Path;
                if (dir is null || !File.Exists(Path.Combine(dir, "MicrosoftGame.config"))) continue;
                var entry = (await package.GetAppListEntriesAsync()).FirstOrDefault();
                if (entry is null) continue;
                var name = entry.DisplayInfo?.DisplayName;
                if (string.IsNullOrWhiteSpace(name)) name = package.DisplayName;
                items.Add(new LibraryItem
                {
                    Id = $"xbox:{package.Id.FamilyName}",
                    Source = "xbox",
                    Name = name,
                    InstallDir = dir,
                    Art = CouchHud.Core.Library.LauncherArtwork.FromPackage(dir),
                    Launch = new LaunchTarget(LaunchKind.Url, $@"shell:AppsFolder\{entry.AppUserModelId}")
                });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or FileNotFoundException or InvalidOperationException)
            {
                // Packages that are being installed or removed throw here; skip them.
            }
        }
        return items;
    }
}
