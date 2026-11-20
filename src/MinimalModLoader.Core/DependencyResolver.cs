using MinimalModLoader.Abstractions;

namespace MinimalModLoader.Core;

public sealed record DependencyResolutionResult(
    IReadOnlyList<DiscoveredMod> OrderedCodeMods,
    IReadOnlyList<DiscoveredMod> ValidContentPacks,
    IReadOnlyList<SkippedMod> SkippedMods,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Validates loader version, duplicate IDs, required/optional dependencies,
/// and computes a deterministic topological load order using Kahn's algorithm.
/// </summary>
public sealed class DependencyResolver
{
    private readonly SemVer _loaderVersion;

    public DependencyResolver(SemVer loaderVersion)
    {
        _loaderVersion = loaderVersion;
    }

    public DependencyResolutionResult Resolve(IReadOnlyList<DiscoveredMod> discovered)
    {
        var skipped = new List<SkippedMod>();
        var warnings = new List<string>();
        var candidates = new Dictionary<string, DiscoveredMod>(StringComparer.OrdinalIgnoreCase);

        // Step 1: Loader version check + duplicate UniqueID detection
        foreach (var mod in discovered.OrderBy(m => m.Manifest.UniqueID, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(mod.Manifest.MinimumLoaderVersion))
            {
                if (!SemVer.TryParse(mod.Manifest.MinimumLoaderVersion, out var minLoader))
                {
                    skipped.Add(new SkippedMod(mod.Manifest.Name, mod.DirectoryPath,
                        $"Invalid MinimumLoaderVersion '{mod.Manifest.MinimumLoaderVersion}'."));
                    continue;
                }

                if (_loaderVersion < minLoader)
                {
                    skipped.Add(new SkippedMod(mod.Manifest.Name, mod.DirectoryPath,
                        $"Requires loader version {minLoader} or newer (current: {_loaderVersion})."));
                    continue;
                }
            }

            if (candidates.TryGetValue(mod.Manifest.UniqueID, out var existing))
            {
                skipped.Add(new SkippedMod(mod.Manifest.Name, mod.DirectoryPath,
                    $"Duplicate UniqueID '{mod.Manifest.UniqueID}' (already loaded from '{existing.DirectoryPath}')."));
                continue;
            }

            candidates[mod.Manifest.UniqueID] = mod;
        }

        // Step 2: Iterative cascade validation for required dependencies and ContentPackFor targets
        bool changed;
        do
        {
            changed = false;
            foreach (var id in candidates.Keys.ToList())
            {
                var mod = candidates[id];
                var failureReason = ValidateRequirements(mod, candidates);
                if (failureReason is not null)
                {
                    candidates.Remove(id);
                    skipped.Add(new SkippedMod(mod.Manifest.Name, mod.DirectoryPath, failureReason));
                    changed = true;
                }
            }
        } while (changed);

        // Step 3: Record non-fatal warnings for missing optional dependencies
        foreach (var mod in candidates.Values)
        {
            foreach (var dep in mod.Manifest.Dependencies.Where(d => !d.IsRequired))
            {
                if (!candidates.TryGetValue(dep.UniqueID, out var target))
                {
                    warnings.Add($"Mod '{mod.Manifest.Name}' has optional dependency '{dep.UniqueID}' which is not installed.");
                }
                else if (!string.IsNullOrWhiteSpace(dep.MinimumVersion) &&
                         SemVer.TryParse(dep.MinimumVersion, out var minVer) &&
                         target.ParsedVersion < minVer)
                {
                    warnings.Add($"Mod '{mod.Manifest.Name}' prefers '{dep.UniqueID}' >= {minVer}, found {target.ParsedVersion}.");
                }
            }
        }

        // Step 4: Separate code mods and content packs; topologically sort code mods
        var codeMods = candidates.Values
            .Where(m => !m.IsContentPack)
            .ToDictionary(m => m.Manifest.UniqueID, StringComparer.OrdinalIgnoreCase);

        var orderedCodeMods = TopologicalSort(codeMods, skipped);
        var survivingCodeModIds = orderedCodeMods
            .Select(m => m.Manifest.UniqueID)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Exclude content packs whose owner was dropped due to a dependency cycle
        var validContentPacks = new List<DiscoveredMod>();
        foreach (var pack in candidates.Values.Where(m => m.IsContentPack)
                     .OrderBy(m => m.Manifest.UniqueID, StringComparer.OrdinalIgnoreCase))
        {
            var ownerId = pack.Manifest.ContentPackFor!.UniqueID;
            if (!survivingCodeModIds.Contains(ownerId))
            {
                skipped.Add(new SkippedMod(pack.Manifest.Name, pack.DirectoryPath,
                    $"ContentPack owner '{ownerId}' could not be loaded."));
                continue;
            }
            validContentPacks.Add(pack);
        }

        return new DependencyResolutionResult(orderedCodeMods, validContentPacks, skipped, warnings);
    }

    private static string? ValidateRequirements(
        DiscoveredMod mod,
        Dictionary<string, DiscoveredMod> candidates)
    {
        if (mod.IsContentPack)
        {
            var target = mod.Manifest.ContentPackFor!;
            if (!candidates.TryGetValue(target.UniqueID, out var owner))
                return $"ContentPack requires missing owner mod '{target.UniqueID}'.";

            if (owner.IsContentPack)
                return $"ContentPack target '{target.UniqueID}' is itself a Content Pack, not a C# mod.";

            if (!string.IsNullOrWhiteSpace(target.MinimumVersion))
            {
                if (!SemVer.TryParse(target.MinimumVersion, out var minVer))
                    return $"Invalid ContentPackFor.MinimumVersion '{target.MinimumVersion}'.";

                if (owner.ParsedVersion < minVer)
                    return $"Requires '{target.UniqueID}' >= {minVer} (found {owner.ParsedVersion}).";
            }
        }

        foreach (var dep in mod.Manifest.Dependencies.Where(d => d.IsRequired))
        {
            if (string.Equals(dep.UniqueID, mod.Manifest.UniqueID, StringComparison.OrdinalIgnoreCase))
                return $"Mod cannot depend on itself ('{dep.UniqueID}').";

            if (!candidates.TryGetValue(dep.UniqueID, out var target))
                return $"Missing required dependency '{dep.UniqueID}'.";

            if (!string.IsNullOrWhiteSpace(dep.MinimumVersion))
            {
                if (!SemVer.TryParse(dep.MinimumVersion, out var minVer))
                    return $"Invalid MinimumVersion '{dep.MinimumVersion}' for dependency '{dep.UniqueID}'.";

                if (target.ParsedVersion < minVer)
                    return $"Dependency '{dep.UniqueID}' requires >= {minVer} (found {target.ParsedVersion}).";
            }
        }

        return null;
    }

    private static List<DiscoveredMod> TopologicalSort(
        Dictionary<string, DiscoveredMod> codeMods,
        List<SkippedMod> skipped)
    {
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in codeMods.Keys)
        {
            inDegree[id] = 0;
            dependents[id] = [];
        }

        // First pass: add all required dependency edges
        foreach (var (id, mod) in codeMods)
        {
            var seenDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dep in mod.Manifest.Dependencies.Where(d => d.IsRequired))
            {
                if (!codeMods.ContainsKey(dep.UniqueID) || !seenDeps.Add(dep.UniqueID))
                    continue;

                dependents[dep.UniqueID].Add(id);
                inDegree[id]++;
            }
        }

        // Second pass: add optional dependency edges only if they do not introduce a cycle
        foreach (var (id, mod) in codeMods)
        {
            foreach (var dep in mod.Manifest.Dependencies.Where(d => !d.IsRequired))
            {
                if (!codeMods.ContainsKey(dep.UniqueID))
                    continue;
                if (string.Equals(dep.UniqueID, id, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (dependents[dep.UniqueID].Contains(id, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (!CanReach(dependents, start: id, target: dep.UniqueID))
                {
                    dependents[dep.UniqueID].Add(id);
                    inDegree[id]++;
                }
            }
        }

        // PriorityQueue ensures deterministic alphabetical order among ready nodes
        var ready = new PriorityQueue<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, deg) in inDegree)
        {
            if (deg == 0)
                ready.Enqueue(id, id);
        }

        var ordered = new List<DiscoveredMod>(codeMods.Count);
        while (ready.Count > 0)
        {
            var currentId = ready.Dequeue();
            ordered.Add(codeMods[currentId]);

            foreach (var nextId in dependents[currentId])
            {
                inDegree[nextId]--;
                if (inDegree[nextId] == 0)
                    ready.Enqueue(nextId, nextId);
            }
        }

        // Any remaining nodes are part of or blocked by a circular dependency
        if (ordered.Count < codeMods.Count)
        {
            var resolvedIds = ordered.Select(m => m.Manifest.UniqueID).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, mod) in codeMods.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!resolvedIds.Contains(id))
                {
                    skipped.Add(new SkippedMod(mod.Manifest.Name, mod.DirectoryPath,
                        $"Circular dependency detected involving '{id}'."));
                }
            }
        }

        return ordered;
    }

    private static bool CanReach(
        Dictionary<string, List<string>> graph,
        string start,
        string target)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(start);
        visited.Add(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!graph.TryGetValue(current, out var neighbors))
                continue;

            foreach (var next in neighbors)
            {
                if (visited.Add(next))
                    queue.Enqueue(next);
            }
        }

        return false;
    }
}
