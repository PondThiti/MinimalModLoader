using System.Text.Json;
using MinimalModLoader.Abstractions;

namespace MinimalModLoader.Core;

internal static class ModPathGuard
{
    public static string ResolveSafePath(string rootDirectory, string relativePath)
    {
        var fullRoot = Path.GetFullPath(rootDirectory);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            fullRoot += Path.DirectorySeparatorChar;

        var combined = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!combined.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Path '{relativePath}' escapes the mod directory.");

        return combined;
    }
}

public sealed class ContentPackImpl : IContentPack
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public ModManifest Manifest { get; }
    public string DirectoryPath { get; }

    public ContentPackImpl(ModManifest manifest, string directoryPath)
    {
        Manifest = manifest;
        DirectoryPath = directoryPath;
    }

    public bool HasFile(string relativePath)
    {
        var fullPath = ModPathGuard.ResolveSafePath(DirectoryPath, relativePath);
        return File.Exists(fullPath);
    }

    public T? ReadJsonFile<T>(string relativePath)
    {
        var fullPath = ModPathGuard.ResolveSafePath(DirectoryPath, relativePath);
        if (!File.Exists(fullPath))
            return default;

        var json = File.ReadAllText(fullPath);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public string ReadTextFile(string relativePath)
    {
        var fullPath = ModPathGuard.ResolveSafePath(DirectoryPath, relativePath);
        return File.ReadAllText(fullPath);
    }
}
