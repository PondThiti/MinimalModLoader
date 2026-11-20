using System.Diagnostics;
using MinimalModLoader.Abstractions;

namespace MinimalModLoader.Core.GameLocator;

/// <summary>
/// Inspects any candidate filesystem path (directory, .apk, .apks, or .xapk) and validates
/// whether it is a recognized game installation or package archive.
/// </summary>
public static class GamePathInspector
{
    public static readonly string[] SupportedArchiveExtensions = [".apk", ".apks", ".xapk"];

    public static bool IsSupportedArchiveFile(string path)
    {
        var ext = Path.GetExtension(path);
        return SupportedArchiveExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase));
    }

    public static GameLocation? Inspect(
        string rawPath,
        GameDiscoveryMethod discoveryMethod,
        GameLocatorOptions options,
        ICollection<string>? diagnostics = null)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(rawPath.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            diagnostics?.Add($"Skipped invalid path '{rawPath}': {ex.Message}");
            return null;
        }

        if (Directory.Exists(fullPath))
            return InspectDirectory(fullPath, discoveryMethod, options);

        if (File.Exists(fullPath))
            return InspectFile(fullPath, discoveryMethod, options, diagnostics);

        return null;
    }

    private static GameLocation? InspectDirectory(
        string dirPath,
        GameDiscoveryMethod discoveryMethod,
        GameLocatorOptions options)
    {
        foreach (var marker in options.ExpectedDesktopMarkers)
        {
            var markerPath = Path.Combine(dirPath, marker);
            if (!File.Exists(markerPath))
                continue;

            var contentDir = Path.Combine(dirPath, "Content");
            var hasContentDir = Directory.Exists(contentDir);
            var version = TryReadFileVersion(markerPath);
            var fileInfo = new FileInfo(markerPath);

            return new GameLocation(
                LocationPath: dirPath,
                SourceKind: GameSourceKind.InstalledDirectory,
                DiscoveryMethod: discoveryMethod,
                DisplayName: Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                PackageOrAssemblyId: marker,
                VersionName: version,
                VersionCode: null,
                ContentDirectory: hasContentDir ? contentDir : null,
                SizeBytes: fileInfo.Length,
                IsVerified: true);
        }

        return null;
    }

    private static GameLocation? InspectFile(
        string filePath,
        GameDiscoveryMethod discoveryMethod,
        GameLocatorOptions options,
        ICollection<string>? diagnostics)
    {
        var ext = Path.GetExtension(filePath);
        if (ext.Equals(".apk", StringComparison.OrdinalIgnoreCase))
        {
            var meta = AndroidBinaryXmlInspector.InspectApkFile(filePath);
            if (meta is null)
            {
                diagnostics?.Add($"Ignored '{Path.GetFileName(filePath)}': not a valid APK or unreadable AndroidManifest.xml.");
                return null;
            }

            if (!MatchesTargetPackage(meta.PackageName, options.TargetAndroidPackageId))
            {
                diagnostics?.Add($"Skipped '{Path.GetFileName(filePath)}': package '{meta.PackageName}' != '{options.TargetAndroidPackageId}'.");
                return null;
            }

            var fileInfo = new FileInfo(filePath);
            return new GameLocation(
                LocationPath: filePath,
                SourceKind: GameSourceKind.AndroidApk,
                DiscoveryMethod: discoveryMethod,
                DisplayName: fileInfo.Name,
                PackageOrAssemblyId: meta.PackageName,
                VersionName: meta.VersionName,
                VersionCode: meta.VersionCode,
                ContentDirectory: null,
                SizeBytes: fileInfo.Length,
                IsVerified: true);
        }

        if (ext.Equals(".apks", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".xapk", StringComparison.OrdinalIgnoreCase))
        {
            var meta = AndroidBinaryXmlInspector.InspectBundleFile(filePath);
            if (meta is null)
            {
                diagnostics?.Add($"Ignored '{Path.GetFileName(filePath)}': not a valid .apks/.xapk bundle.");
                return null;
            }

            if (!MatchesTargetPackage(meta.PackageName, options.TargetAndroidPackageId))
            {
                diagnostics?.Add($"Skipped '{Path.GetFileName(filePath)}': bundle package '{meta.PackageName}' != '{options.TargetAndroidPackageId}'.");
                return null;
            }

            var fileInfo = new FileInfo(filePath);
            return new GameLocation(
                LocationPath: filePath,
                SourceKind: GameSourceKind.AndroidBundle,
                DiscoveryMethod: discoveryMethod,
                DisplayName: fileInfo.Name,
                PackageOrAssemblyId: meta.PackageName,
                VersionName: meta.VersionName,
                VersionCode: meta.VersionCode,
                ContentDirectory: null,
                SizeBytes: fileInfo.Length,
                IsVerified: true);
        }

        // If user pointed explicitly at a desktop game executable/DLL, inspect its parent directory
        var fileName = Path.GetFileName(filePath);
        if (options.ExpectedDesktopMarkers.Any(m => m.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
        {
            var parentDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(parentDir))
                return InspectDirectory(parentDir, discoveryMethod, options);
        }

        return null;
    }

    private static bool MatchesTargetPackage(string packageName, string? expectedPackage)
        => string.IsNullOrWhiteSpace(expectedPackage) ||
           string.Equals(packageName, expectedPackage, StringComparison.OrdinalIgnoreCase);

    private static string? TryReadFileVersion(string filePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(filePath);
            var fileVer = info.FileVersion?.Trim();
            var prodVer = info.ProductVersion?.Trim();

            if (!string.IsNullOrWhiteSpace(prodVer) && !prodVer.Contains(','))
                return prodVer;
            if (!string.IsNullOrWhiteSpace(fileVer) && !fileVer.Contains(','))
                return fileVer;
            if (!string.IsNullOrWhiteSpace(prodVer))
                return NormalizeCommaVersion(prodVer);
            if (!string.IsNullOrWhiteSpace(fileVer))
                return NormalizeCommaVersion(fileVer);
        }
        catch
        {
            // Ignore non-PE or unreadable version metadata
        }
        return null;
    }

    private static string NormalizeCommaVersion(string raw)
    {
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? string.Join('.', parts) : raw;
    }
}
