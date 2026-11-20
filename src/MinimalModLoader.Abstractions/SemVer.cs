namespace MinimalModLoader.Abstractions;

/// <summary>
/// Lightweight semantic version parser supporting Major.Minor.Patch[-prerelease].
/// </summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string? PreRelease = null) : IComparable<SemVer>
{
    public static bool TryParse(string? input, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();
        string? preRelease = null;
        var dashIdx = trimmed.IndexOf('-');
        if (dashIdx >= 0)
        {
            preRelease = trimmed[(dashIdx + 1)..];
            trimmed = trimmed[..dashIdx];
            if (string.IsNullOrWhiteSpace(preRelease))
                return false;
        }

        var parts = trimmed.Split('.');
        if (parts.Length is < 2 or > 3)
            return false;

        if (!int.TryParse(parts[0], out var major) || major < 0)
            return false;
        if (!int.TryParse(parts[1], out var minor) || minor < 0)
            return false;

        var patch = 0;
        if (parts.Length == 3 && (!int.TryParse(parts[2], out patch) || patch < 0))
            return false;

        version = new SemVer(major, minor, patch, preRelease);
        return true;
    }

    public static SemVer Parse(string input)
        => TryParse(input, out var v) ? v : throw new FormatException($"Invalid semantic version: '{input}'.");

    public int CompareTo(SemVer other)
    {
        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;
        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;
        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0) return cmp;

        // Release version > prerelease version (per SemVer 2.0)
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;
        return StringComparer.OrdinalIgnoreCase.Compare(PreRelease, other.PreRelease);
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public override string ToString()
        => PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}
