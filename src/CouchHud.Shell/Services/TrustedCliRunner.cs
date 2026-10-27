using CouchHud.Core.Updates;

namespace CouchHud.Shell.Services;

/// <summary>
/// The Windows side of <see cref="CouchHud.Core.Platform.TrustedCli"/>, shared by CaseHUD's API switch and the WebView2 runtime choice:
/// the real path of a file through an open handle and the trusted roots (Program Files).
/// </summary>
internal static class TrustedCliRunner
{
    /// <summary>The real path of a file through an open handle (links and short names resolved); the text itself if it cannot be opened.</summary>
    public static string CanonicalPath(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return PathSafety.StripPrefix(CouchHud.Privileged.SetupProbe.FinalPath(file.SafeFileHandle));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    /// <summary>Program Files (64 and 32 bit), as full paths.</summary>
    public static IEnumerable<string> TrustedRoots() =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Where(r => !string.IsNullOrEmpty(r)).Select(r => Path.GetFullPath(r)).Distinct(StringComparer.OrdinalIgnoreCase);
}
