using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MinimalModLoader.Core.GameLocator;

/// <summary>
/// Metadata extracted from an Android APK, APKS, or XAPK package without unpacking the full archive.
/// </summary>
public sealed record AndroidPackageMetadata(
    string PackageName,
    string? VersionName,
    long VersionCode,
    bool IsBundle,
    int InnerApkCount);

/// <summary>
/// Lightweight, dependency-free parser for Android binary XML (<c>AndroidManifest.xml</c>)
/// and split bundle manifests (<c>.xapk</c> / <c>.apks</c>).
/// </summary>
public static class AndroidBinaryXmlInspector
{
    private const ushort ResXmlType = 0x0003;
    private const ushort ResStringPoolType = 0x0001;
    private const ushort ResXmlResourceMapType = 0x0180;
    private const ushort ResXmlStartElementType = 0x0102;
    private const uint StringPoolUtf8Flag = 0x0100;

    private const uint AttrIdVersionCode = 0x0101021b;
    private const uint AttrIdVersionName = 0x0101021c;
    private const uint AttrIdVersionCodeMajor = 0x01010576;

    private const byte TypeString = 0x03;
    private const byte TypeIntDec = 0x10;
    private const byte TypeIntHex = 0x11;

    /// <summary>
    /// Inspects a standalone <c>.apk</c> file on disk and reads <c>package</c>, <c>versionName</c>,
    /// and <c>versionCode</c> from its binary <c>AndroidManifest.xml</c>.
    /// </summary>
    public static AndroidPackageMetadata? InspectApkFile(string apkPath)
    {
        try
        {
            using var fs = new FileStream(apkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return InspectApkStream(fs);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Inspects a seekable APK stream (or ZipArchive) without extracting the archive to disk.
    /// </summary>
    public static AndroidPackageMetadata? InspectApkStream(Stream apkStream)
    {
        try
        {
            using var zip = new ZipArchive(apkStream, ZipArchiveMode.Read, leaveOpen: true);
            var manifestEntry = zip.GetEntry("AndroidManifest.xml");
            if (manifestEntry is null || manifestEntry.Length <= 0 || manifestEntry.Length > 8 * 1024 * 1024)
                return null;

            using var ms = new MemoryStream((int)Math.Min(manifestEntry.Length, 1024 * 1024));
            using (var entryStream = manifestEntry.Open())
                entryStream.CopyTo(ms);

            var parsed = ParseBinaryManifest(ms.ToArray());
            return parsed is null ? null : parsed with { IsBundle = false, InnerApkCount = 1 };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Inspects an <c>.apks</c> or <c>.xapk</c> split bundle archive via its metadata JSON
    /// or nested base APK's binary <c>AndroidManifest.xml</c>.
    /// </summary>
    public static AndroidPackageMetadata? InspectBundleFile(string bundlePath)
    {
        try
        {
            using var fs = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

            var innerApks = zip.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) && e.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (innerApks.Count == 0)
                return null;

            // 1) Try XAPK manifest.json or SAI meta.sai.json / info.json
            var jsonMeta = TryReadBundleJsonMetadata(zip, innerApks.Count);
            if (jsonMeta is not null)
                return jsonMeta;

            // 2) Fallback: inspect nested base.apk / non-split APK directly via in-memory stream
            var baseEntry = innerApks
                .OrderByDescending(e => e.Name.Equals("base.apk", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(e => e.Name.Equals("base-master.apk", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(e => !e.Name.StartsWith("split_", StringComparison.OrdinalIgnoreCase) &&
                                        !e.Name.Contains("config.", StringComparison.OrdinalIgnoreCase))
                .First();

            byte[]? manifestBytes;
            using (var nestedStream = baseEntry.Open())
            {
                manifestBytes = ExtractManifestBytesFromNestedZipStream(nestedStream);
            }

            if (manifestBytes is not null)
            {
                var parsed = ParseBinaryManifest(manifestBytes);
                if (parsed is not null)
                    return parsed with { IsBundle = true, InnerApkCount = innerApks.Count };
            }

            // Fallback when nested APK uses streaming data descriptors (compressedSize == 0 in local header)
            if (baseEntry.Length > 0 && baseEntry.Length <= 100L * 1024 * 1024)
            {
                using var ms = new MemoryStream((int)baseEntry.Length);
                using (var nestedStream = baseEntry.Open())
                    nestedStream.CopyTo(ms);
                ms.Position = 0;
                var parsed = InspectApkStream(ms);
                if (parsed is not null)
                    return parsed with { IsBundle = true, InnerApkCount = innerApks.Count };
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses raw Android Binary XML bytes of <c>AndroidManifest.xml</c>.
    /// </summary>
    public static AndroidPackageMetadata? ParseBinaryManifest(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
            return null;

        var docType = BinaryPrimitives.ReadUInt16LittleEndian(data[0..2]);
        var docHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data[2..4]);
        if (docType != ResXmlType || docHeaderSize < 8 || docHeaderSize > data.Length)
            return null;

        string[]? stringPool = null;
        uint[]? resourceMap = null;

        var offset = (int)docHeaderSize;
        while (offset + 8 <= data.Length)
        {
            var chunkType = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
            var chunkHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 2, 2));
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 4, 4));

            if (chunkSize < 8 || chunkHeaderSize < 8 || chunkHeaderSize > chunkSize || offset + chunkSize > data.Length)
                break;

            var chunk = data.Slice(offset, chunkSize);

            switch (chunkType)
            {
                case ResStringPoolType:
                    stringPool = ParseStringPool(chunk, chunkHeaderSize);
                    break;

                case ResXmlResourceMapType:
                    resourceMap = ParseResourceMap(chunk, chunkHeaderSize);
                    break;

                case ResXmlStartElementType:
                    if (stringPool is not null)
                    {
                        var manifest = TryParseManifestElement(chunk, chunkHeaderSize, stringPool, resourceMap);
                        if (manifest is not null)
                            return manifest;
                    }
                    break;
            }

            offset += chunkSize;
        }

        return null;
    }

    private static string[] ParseStringPool(ReadOnlySpan<byte> chunk, int headerSize)
    {
        if (chunk.Length < 28)
            return [];

        var stringCount = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(8, 4));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(16, 4));
        var stringsStart = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(20, 4));
        var isUtf8 = (flags & StringPoolUtf8Flag) != 0;

        if (stringCount > 200_000 || headerSize + stringCount * 4L > chunk.Length || stringsStart >= chunk.Length)
            return [];

        var result = new string[stringCount];
        var offsetsBase = headerSize;
        var dataBase = (int)stringsStart;

        for (var i = 0; i < (int)stringCount; i++)
        {
            var relOffset = BinaryPrimitives.ReadInt32LittleEndian(chunk.Slice(offsetsBase + i * 4, 4));
            var pos = dataBase + relOffset;
            if (pos < dataBase || pos >= chunk.Length)
            {
                result[i] = string.Empty;
                continue;
            }

            result[i] = isUtf8
                ? ReadUtf8PoolString(chunk, pos)
                : ReadUtf16PoolString(chunk, pos);
        }

        return result;
    }

    private static string ReadUtf8PoolString(ReadOnlySpan<byte> chunk, int pos)
    {
        if (pos >= chunk.Length)
            return string.Empty;

        // 1) Skip UTF-16 character length (1 or 2 bytes)
        var first = chunk[pos++];
        if ((first & 0x80) != 0)
        {
            if (pos >= chunk.Length) return string.Empty;
            pos++;
        }

        // 2) Read UTF-8 byte length (1 or 2 bytes)
        if (pos >= chunk.Length)
            return string.Empty;

        int byteLen = chunk[pos++];
        if ((byteLen & 0x80) != 0)
        {
            if (pos >= chunk.Length) return string.Empty;
            byteLen = ((byteLen & 0x7F) << 8) | chunk[pos++];
        }

        if (byteLen <= 0 || pos + byteLen > chunk.Length)
            return string.Empty;

        return Encoding.UTF8.GetString(chunk.Slice(pos, byteLen));
    }

    private static string ReadUtf16PoolString(ReadOnlySpan<byte> chunk, int pos)
    {
        if (pos + 2 > chunk.Length)
            return string.Empty;

        int charLen = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(pos, 2));
        pos += 2;
        if ((charLen & 0x8000) != 0)
        {
            if (pos + 2 > chunk.Length) return string.Empty;
            charLen = ((charLen & 0x7FFF) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(pos, 2));
            pos += 2;
        }

        var byteLen = charLen * 2;
        if (byteLen <= 0 || pos + byteLen > chunk.Length)
            return string.Empty;

        return Encoding.Unicode.GetString(chunk.Slice(pos, byteLen));
    }

    private static uint[] ParseResourceMap(ReadOnlySpan<byte> chunk, int headerSize)
    {
        var payloadLen = chunk.Length - headerSize;
        if (payloadLen < 4)
            return [];

        var count = payloadLen / 4;
        var map = new uint[count];
        for (var i = 0; i < count; i++)
            map[i] = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(headerSize + i * 4, 4));
        return map;
    }

    private static AndroidPackageMetadata? TryParseManifestElement(
        ReadOnlySpan<byte> chunk,
        int headerSize,
        string[] stringPool,
        uint[]? resourceMap)
    {
        if (headerSize + 20 > chunk.Length)
            return null;

        var ext = chunk.Slice(headerSize);
        var elemNameIdx = BinaryPrimitives.ReadInt32LittleEndian(ext.Slice(4, 4));
        var elemName = GetPoolString(stringPool, elemNameIdx);
        if (!string.Equals(elemName, "manifest", StringComparison.Ordinal))
            return null;

        var attrStart = BinaryPrimitives.ReadUInt16LittleEndian(ext.Slice(8, 2));
        var attrSize = BinaryPrimitives.ReadUInt16LittleEndian(ext.Slice(10, 2));
        var attrCount = BinaryPrimitives.ReadUInt16LittleEndian(ext.Slice(12, 2));

        if (attrSize < 20)
            return null;

        string? packageName = null;
        string? versionName = null;
        int versionCodeLow = 0;
        int versionCodeMajor = 0;

        for (var i = 0; i < attrCount; i++)
        {
            var attrPos = attrStart + i * attrSize;
            if (attrPos + 20 > ext.Length)
                break;

            var attr = ext.Slice(attrPos, 20);
            var attrNsIdx = BinaryPrimitives.ReadInt32LittleEndian(attr.Slice(0, 4));
            var attrNameIdx = BinaryPrimitives.ReadInt32LittleEndian(attr.Slice(4, 4));
            var attrRawIdx = BinaryPrimitives.ReadInt32LittleEndian(attr.Slice(8, 4));
            var valueType = attr[15];
            var valueData = BinaryPrimitives.ReadInt32LittleEndian(attr.Slice(16, 4));

            var attrName = GetPoolString(stringPool, attrNameIdx);
            var resId = resourceMap is not null && attrNameIdx >= 0 && attrNameIdx < resourceMap.Length
                ? resourceMap[attrNameIdx]
                : 0u;

            if (attrNsIdx < 0 && string.Equals(attrName, "package", StringComparison.Ordinal))
            {
                packageName = ReadAttrString(stringPool, attrRawIdx, valueType, valueData);
            }
            else if (resId == AttrIdVersionName || string.Equals(attrName, "versionName", StringComparison.Ordinal))
            {
                versionName = ReadAttrString(stringPool, attrRawIdx, valueType, valueData)
                    ?? (valueType is TypeIntDec or TypeIntHex ? valueData.ToString() : null);
            }
            else if (resId == AttrIdVersionCode || string.Equals(attrName, "versionCode", StringComparison.Ordinal))
            {
                if (valueType is TypeIntDec or TypeIntHex)
                    versionCodeLow = valueData;
                else if (int.TryParse(ReadAttrString(stringPool, attrRawIdx, valueType, valueData), out var parsedCode))
                    versionCodeLow = parsedCode;
            }
            else if (resId == AttrIdVersionCodeMajor || string.Equals(attrName, "versionCodeMajor", StringComparison.Ordinal))
            {
                if (valueType is TypeIntDec or TypeIntHex)
                    versionCodeMajor = valueData;
            }
        }

        if (string.IsNullOrWhiteSpace(packageName))
            return null;

        var longVersionCode = versionCodeMajor != 0
            ? ((long)versionCodeMajor << 32) | (uint)versionCodeLow
            : versionCodeLow;

        return new AndroidPackageMetadata(
            PackageName: packageName,
            VersionName: versionName,
            VersionCode: longVersionCode,
            IsBundle: false,
            InnerApkCount: 1);
    }

    private static string? ReadAttrString(string[] pool, int rawIdx, byte valueType, int valueData)
    {
        if (rawIdx >= 0 && rawIdx < pool.Length)
            return pool[rawIdx];
        if (valueType == TypeString && valueData >= 0 && valueData < pool.Length)
            return pool[valueData];
        return null;
    }

    private static string? GetPoolString(string[] pool, int index)
        => index >= 0 && index < pool.Length ? pool[index] : null;

    private static AndroidPackageMetadata? TryReadBundleJsonMetadata(ZipArchive zip, int innerApkCount)
    {
        var jsonEntry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
            e.FullName.Equals("meta.sai.json", StringComparison.OrdinalIgnoreCase) ||
            e.FullName.Equals("info.json", StringComparison.OrdinalIgnoreCase));

        if (jsonEntry is null || jsonEntry.Length > 2 * 1024 * 1024)
            return null;

        try
        {
            using var stream = jsonEntry.Open();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var pkg = TryGetString(root, "package_name", "package", "packageName", "pname");
            if (string.IsNullOrWhiteSpace(pkg))
                return null;

            var verName = TryGetString(root, "version_name", "versionName", "version");
            var verCode = TryGetLong(root, "version_code", "versionCode");

            return new AndroidPackageMetadata(
                PackageName: pkg,
                VersionName: verName,
                VersionCode: verCode,
                IsBundle: true,
                InnerApkCount: innerApkCount);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement obj, params string[] names)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                {
                    var val = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        return val;
                }
            }
        }
        return null;
    }

    private static long TryGetLong(JsonElement obj, params string[] names)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (!prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out var n))
                    return n;
                if (prop.Value.ValueKind == JsonValueKind.String && long.TryParse(prop.Value.GetString(), out var s))
                    return s;
            }
        }
        return 0;
    }

    /// <summary>
    /// Streams local ZIP file headers sequentially from a non-seekable nested APK stream
    /// to extract only <c>AndroidManifest.xml</c> without buffering the whole APK in RAM or disk.
    /// </summary>
    private static byte[]? ExtractManifestBytesFromNestedZipStream(Stream stream)
    {
        // Local file header signature: 0x04034b50
        var header = new byte[30];
        var entriesChecked = 0;

        while (entriesChecked < 5000)
        {
            if (!ReadExact(stream, header, 30))
                return null;

            var sig = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
            if (sig != 0x04034b50)
                return null;

            entriesChecked++;
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
            var compressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2));
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(18, 4));
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26, 2));
            var extraLen = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));

            var nameBytes = new byte[nameLen];
            if (!ReadExact(stream, nameBytes, nameLen))
                return null;
            if (extraLen > 0 && !SkipExact(stream, extraLen))
                return null;

            var entryName = Encoding.UTF8.GetString(nameBytes);
            var hasDataDescriptor = (flags & 0x08) != 0;

            if (entryName == "AndroidManifest.xml" && compressedSize > 0 && compressedSize < 8 * 1024 * 1024)
            {
                var payload = new byte[compressedSize];
                if (!ReadExact(stream, payload, (int)compressedSize))
                    return null;

                if (compressionMethod == 0)
                    return payload;

                if (compressionMethod == 8)
                {
                    using var compressedMs = new MemoryStream(payload);
                    using var deflate = new DeflateStream(compressedMs, CompressionMode.Decompress);
                    using var outMs = new MemoryStream();
                    deflate.CopyTo(outMs);
                    return outMs.ToArray();
                }

                return null;
            }

            // Cannot safely skip unknown-size entry with data descriptor in forward-only stream
            if (hasDataDescriptor && compressedSize == 0)
                return null;

            if (!SkipExact(stream, compressedSize))
                return null;

            if (hasDataDescriptor)
            {
                // Skip optional 12- or 16-byte data descriptor (peek signature 0x08074b50)
                var dd = new byte[12];
                if (!ReadExact(stream, dd, 12))
                    return null;
                if (BinaryPrimitives.ReadUInt32LittleEndian(dd.AsSpan(0, 4)) == 0x08074b50)
                {
                    if (!SkipExact(stream, 4))
                        return null;
                }
            }
        }

        return null;
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
                return false;
            offset += read;
        }
        return true;
    }

    private static bool SkipExact(Stream stream, long count)
    {
        var scratch = new byte[8192];
        var remaining = count;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(scratch.Length, remaining);
            var read = stream.Read(scratch, 0, toRead);
            if (read <= 0)
                return false;
            remaining -= read;
        }
        return true;
    }
}
