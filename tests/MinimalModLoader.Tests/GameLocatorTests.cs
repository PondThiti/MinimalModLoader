using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MinimalModLoader.Abstractions;
using MinimalModLoader.Core.GameLocator;
using Xunit;

namespace MinimalModLoader.Tests;

public sealed class GameLocatorTests : IDisposable
{
    private readonly string _tempRoot;

    public GameLocatorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "MinimalModLoader_GameLocatorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Ignore cleanup lock in temp
        }
    }

    [Fact]
    public void BinaryXmlInspector_ParsesUtf16AndUtf8Manifests_AndRejectsOtherPackages()
    {
        var utf16Apk = Path.Combine(_tempRoot, "game_utf16.apk");
        CreateSyntheticApk(utf16Apk, "com.chucklefish.stardewvalley", "1.6.15.3", 161503, useUtf8Pool: false);

        var utf8Apk = Path.Combine(_tempRoot, "game_utf8.apk");
        CreateSyntheticApk(utf8Apk, "com.chucklefish.stardewvalley", "1.6.16.0", 161600, useUtf8Pool: true);

        var wrongApk = Path.Combine(_tempRoot, "other_app.apk");
        CreateSyntheticApk(wrongApk, "com.example.otherapp", "2.0.0", 200, useUtf8Pool: false);

        var meta16 = AndroidBinaryXmlInspector.InspectApkFile(utf16Apk);
        Assert.NotNull(meta16);
        Assert.Equal("com.chucklefish.stardewvalley", meta16!.PackageName);
        Assert.Equal("1.6.15.3", meta16.VersionName);
        Assert.Equal(161503, meta16.VersionCode);

        var meta8 = AndroidBinaryXmlInspector.InspectApkFile(utf8Apk);
        Assert.NotNull(meta8);
        Assert.Equal("com.chucklefish.stardewvalley", meta8!.PackageName);
        Assert.Equal("1.6.16.0", meta8.VersionName);
        Assert.Equal(161600, meta8.VersionCode);

        var opts = new GameLocatorOptions
        {
            IncludeStandardInstallPaths = false,
            ScanDrivesAndCommonFolders = false
        };
        Assert.NotNull(GamePathInspector.Inspect(utf16Apk, GameDiscoveryMethod.CommandLineArgument, opts));
        Assert.Null(GamePathInspector.Inspect(wrongApk, GameDiscoveryMethod.CommandLineArgument, opts));
    }

    [Fact]
    public void BundleInspector_DetectsXapkManifestAndNestedApksBase()
    {
        // 1. .xapk with manifest.json + inner apk
        var xapkPath = Path.Combine(_tempRoot, "stardew.xapk");
        using (var zip = ZipFile.Open(xapkPath, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry("manifest.json");
            using (var sw = new StreamWriter(manifestEntry.Open(), Encoding.UTF8))
            {
                sw.Write("""
                {
                  "package_name": "com.chucklefish.stardewvalley",
                  "version_name": "1.6.15.3",
                  "version_code": "161503"
                }
                """);
            }
            var dummyApk = zip.CreateEntry("com.chucklefish.stardewvalley.apk");
            using var s = dummyApk.Open();
            s.Write([0x50, 0x4B, 0x05, 0x06]);
        }

        var xapkMeta = AndroidBinaryXmlInspector.InspectBundleFile(xapkPath);
        Assert.NotNull(xapkMeta);
        Assert.True(xapkMeta!.IsBundle);
        Assert.Equal("com.chucklefish.stardewvalley", xapkMeta.PackageName);
        Assert.Equal("1.6.15.3", xapkMeta.VersionName);
        Assert.Equal(161503, xapkMeta.VersionCode);

        // 2. .apks without JSON metadata, containing nested base.apk with binary AndroidManifest.xml
        var innerBaseApk = Path.Combine(_tempRoot, "base.apk");
        CreateSyntheticApk(innerBaseApk, "com.chucklefish.stardewvalley", "1.6.14.0", 161400, useUtf8Pool: true);

        var apksPath = Path.Combine(_tempRoot, "stardew_splits.apks");
        using (var zip = ZipFile.Open(apksPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(innerBaseApk, "splits/base-master.apk", CompressionLevel.NoCompression);
            var split = zip.CreateEntry("splits/split_config.arm64_v8a.apk");
            using var s = split.Open();
            s.Write([0x50, 0x4B, 0x05, 0x06]);
        }

        var apksMeta = AndroidBinaryXmlInspector.InspectBundleFile(apksPath);
        Assert.NotNull(apksMeta);
        Assert.True(apksMeta!.IsBundle);
        Assert.Equal(2, apksMeta.InnerApkCount);
        Assert.Equal("com.chucklefish.stardewvalley", apksMeta.PackageName);
        Assert.Equal("1.6.14.0", apksMeta.VersionName);
        Assert.Equal(161400, apksMeta.VersionCode);
    }

    [Fact]
    public void GameLocator_RespectsPriority_ExplicitOverEnvOverConfigOverScan()
    {
        var cliDir = CreateFakeDesktopGameDir("CliGame");
        var envDir = CreateFakeDesktopGameDir("EnvGame");
        var cfgDir = CreateFakeDesktopGameDir("CfgGame");
        var scanRoot = Path.Combine(_tempRoot, "ScanRoot");
        var scannedApk = Path.Combine(scanRoot, "Nested", "Downloads", "any_random_name.apk");
        Directory.CreateDirectory(Path.GetDirectoryName(scannedApk)!);
        CreateSyntheticApk(scannedApk, "com.chucklefish.stardewvalley", "1.6.15.3", 161503, useUtf8Pool: false);

        var cfgFile = Path.Combine(_tempRoot, "game-locator.json");
        var envVars = new Dictionary<string, string?>
        {
            [GameLocatorOptions.DefaultEnvironmentVariable] = envDir
        };

        var locator = new GameLocator(GameLocator.CreateDefaultProviders(name => envVars.GetValueOrDefault(name)));
        locator.SaveConfiguredPath(cfgFile, cfgDir);

        // 1) With ExplicitPath set -> CLI wins, and all 4 candidates are discovered
        var reportAll = locator.LocateAll(new GameLocatorOptions
        {
            ExplicitPath = cliDir,
            ConfigFilePath = cfgFile,
            AdditionalSearchRoots = [scanRoot],
            IncludeStandardInstallPaths = false,
            ScanDrivesAndCommonFolders = false
        });
        Assert.NotNull(reportAll.Selected);
        Assert.Equal(GameDiscoveryMethod.CommandLineArgument, reportAll.Selected!.DiscoveryMethod);
        Assert.Equal(Path.GetFullPath(cliDir), reportAll.Selected.LocationPath);
        Assert.Equal(4, reportAll.Candidates.Count);

        // 2) Without ExplicitPath -> EnvVar wins
        var reportEnv = locator.LocateAll(new GameLocatorOptions
        {
            ConfigFilePath = cfgFile,
            AdditionalSearchRoots = [scanRoot],
            IncludeStandardInstallPaths = false,
            ScanDrivesAndCommonFolders = false
        });
        Assert.Equal(GameDiscoveryMethod.EnvironmentVariable, reportEnv.Selected!.DiscoveryMethod);

        // 3) Without EnvVar -> SavedConfig wins
        envVars.Clear();
        var reportCfg = locator.LocateAll(new GameLocatorOptions
        {
            ConfigFilePath = cfgFile,
            AdditionalSearchRoots = [scanRoot],
            IncludeStandardInstallPaths = false,
            ScanDrivesAndCommonFolders = false
        });
        Assert.Equal(GameDiscoveryMethod.SavedConfiguration, reportCfg.Selected!.DiscoveryMethod);

        // 4) Without Config -> FileSystemScan discovers the renamed APK by real package ID
        var reportScan = locator.LocateAll(new GameLocatorOptions
        {
            AdditionalSearchRoots = [scanRoot],
            IncludeStandardInstallPaths = false,
            ScanDrivesAndCommonFolders = false
        });
        Assert.NotNull(reportScan.Selected);
        Assert.Equal(GameDiscoveryMethod.FileSystemScan, reportScan.Selected!.DiscoveryMethod);
        Assert.Equal(GameSourceKind.AndroidApk, reportScan.Selected.SourceKind);
        Assert.Equal("com.chucklefish.stardewvalley", reportScan.Selected.PackageOrAssemblyId);
    }

    [Fact]
    public void GameLocator_SupportsCustomPlatformProviders_ForFutureAndroidHost()
    {
        var fakeAndroidProvider = new FakeAndroidPackageProvider(
            new GameLocation(
                LocationPath: "content://com.android.externalstorage.documents/document/primary%3AStardew.apk",
                SourceKind: GameSourceKind.AndroidApk,
                DiscoveryMethod: GameDiscoveryMethod.CustomProvider,
                DisplayName: "Stardew.apk (SAF)",
                PackageOrAssemblyId: "com.chucklefish.stardewvalley",
                VersionName: "1.6.15.3",
                VersionCode: 161503));

        var locator = new GameLocator(GameLocator.CreateDefaultProviders(_ => null))
            .AddProvider(fakeAndroidProvider);

        var report = locator.LocateAll(new GameLocatorOptions
        {
            IncludeStandardInstallPaths = false,
            ScanDrivesAndCommonFolders = false
        });

        Assert.NotNull(report.Selected);
        Assert.Equal(GameDiscoveryMethod.CustomProvider, report.Selected!.DiscoveryMethod);
        Assert.StartsWith("content://", report.Selected.LocationPath);
    }

    private string CreateFakeDesktopGameDir(string folderName)
    {
        var dir = Path.Combine(_tempRoot, folderName);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "Content"));
        File.WriteAllText(Path.Combine(dir, "Stardew Valley.dll"), "fake-assembly-marker");
        return dir;
    }

    private static void CreateSyntheticApk(
        string apkPath,
        string packageName,
        string versionName,
        int versionCode,
        bool useUtf8Pool)
    {
        var axml = BuildMinimalBinaryManifest(packageName, versionName, versionCode, useUtf8Pool);
        using var zip = ZipFile.Open(apkPath, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("AndroidManifest.xml", CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(axml, 0, axml.Length);
    }

    private static byte[] BuildMinimalBinaryManifest(
        string packageName,
        string versionName,
        int versionCode,
        bool useUtf8)
    {
        // Strings: 0=versionCode, 1=versionName, 2=android ns, 3=manifest, 4=package, 5=<pkg>, 6=<verName>
        string[] strings =
        [
            "versionCode",
            "versionName",
            "http://schemas.android.com/apk/res/android",
            "manifest",
            "package",
            packageName,
            versionName
        ];

        var spChunk = BuildStringPoolChunk(strings, useUtf8);
        var resMapChunk = BuildResourceMapChunk([0x0101021bu, 0x0101021cu]);
        var startElemChunk = BuildManifestStartElementChunk(versionCode);

        var totalSize = 8 + spChunk.Length + resMapChunk.Length + startElemChunk.Length;
        var doc = new byte[totalSize];
        BinaryPrimitives.WriteUInt16LittleEndian(doc.AsSpan(0, 2), 0x0003); // RES_XML_TYPE
        BinaryPrimitives.WriteUInt16LittleEndian(doc.AsSpan(2, 2), 8);
        BinaryPrimitives.WriteInt32LittleEndian(doc.AsSpan(4, 4), totalSize);

        var pos = 8;
        spChunk.CopyTo(doc, pos); pos += spChunk.Length;
        resMapChunk.CopyTo(doc, pos); pos += resMapChunk.Length;
        startElemChunk.CopyTo(doc, pos);
        return doc;
    }

    private static byte[] BuildStringPoolChunk(string[] strings, bool useUtf8)
    {
        using var strData = new MemoryStream();
        var offsets = new int[strings.Length];

        for (var i = 0; i < strings.Length; i++)
        {
            offsets[i] = (int)strData.Position;
            var s = strings[i];
            if (useUtf8)
            {
                var utf8 = Encoding.UTF8.GetBytes(s);
                strData.WriteByte((byte)s.Length);
                strData.WriteByte((byte)utf8.Length);
                strData.Write(utf8);
                strData.WriteByte(0);
            }
            else
            {
                var utf16 = Encoding.Unicode.GetBytes(s);
                var lenBuf = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(lenBuf, (ushort)s.Length);
                strData.Write(lenBuf);
                strData.Write(utf16);
                strData.Write([0, 0]);
            }
        }

        // Align string data to 4-byte boundary
        while (strData.Length % 4 != 0)
            strData.WriteByte(0);

        const int headerSize = 28;
        var offsetsSize = strings.Length * 4;
        var stringsStart = headerSize + offsetsSize;
        var rawStrings = strData.ToArray();
        var chunkSize = stringsStart + rawStrings.Length;

        var chunk = new byte[chunkSize];
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(0, 2), 0x0001); // RES_STRING_POOL_TYPE
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(2, 2), headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4, 4), chunkSize);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(8, 4), (uint)strings.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(12, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(16, 4), useUtf8 ? 0x0100u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(20, 4), (uint)stringsStart);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(24, 4), 0);

        for (var i = 0; i < offsets.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(headerSize + i * 4, 4), offsets[i]);

        rawStrings.CopyTo(chunk, stringsStart);
        return chunk;
    }

    private static byte[] BuildResourceMapChunk(uint[] ids)
    {
        var size = 8 + ids.Length * 4;
        var chunk = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(0, 2), 0x0180);
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(2, 2), 8);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4, 4), size);
        for (var i = 0; i < ids.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(8 + i * 4, 4), ids[i]);
        return chunk;
    }

    private static byte[] BuildManifestStartElementChunk(int versionCode)
    {
        // Header (16) + AttrExt (20) + 3 attributes * 20 = 96 bytes
        const int headerSize = 16;
        const int extHeader = 20;
        const int attrCount = 3;
        const int size = headerSize + extHeader + attrCount * 20;

        var chunk = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(0, 2), 0x0102); // START_ELEMENT
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(2, 2), headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4, 4), size);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(8, 4), 1); // lineNumber
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(12, 4), -1); // comment

        // ResXMLTree_attrExt
        var ext = chunk.AsSpan(headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(ext.Slice(0, 4), -1); // ns
        BinaryPrimitives.WriteInt32LittleEndian(ext.Slice(4, 4), 3);  // "manifest"
        BinaryPrimitives.WriteUInt16LittleEndian(ext.Slice(8, 2), 20); // attributeStart
        BinaryPrimitives.WriteUInt16LittleEndian(ext.Slice(10, 2), 20); // attributeSize
        BinaryPrimitives.WriteUInt16LittleEndian(ext.Slice(12, 2), attrCount);

        // Attr 0: package="<packageName>"
        WriteAttr(ext.Slice(20, 20), ns: -1, nameIdx: 4, rawIdx: 5, dataType: 0x03, data: 5);
        // Attr 1: android:versionName="<versionName>"
        WriteAttr(ext.Slice(40, 20), ns: 2, nameIdx: 1, rawIdx: 6, dataType: 0x03, data: 6);
        // Attr 2: android:versionCode=<versionCode>
        WriteAttr(ext.Slice(60, 20), ns: 2, nameIdx: 0, rawIdx: -1, dataType: 0x10, data: versionCode);

        return chunk;
    }

    private static void WriteAttr(Span<byte> dst, int ns, int nameIdx, int rawIdx, byte dataType, int data)
    {
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(0, 4), ns);
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(4, 4), nameIdx);
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(8, 4), rawIdx);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(12, 2), 8); // typedValue.size
        dst[14] = 0;
        dst[15] = dataType;
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(16, 4), data);
    }

    private sealed class FakeAndroidPackageProvider(GameLocation location) : IGameLocationProvider
    {
        public string ProviderName => "FakeAndroidProvider";
        public int Priority => 35;

        public IEnumerable<GameLocation> Discover(
            GameLocatorOptions options,
            ICollection<string> searchedLocations,
            ICollection<string> diagnostics)
        {
            searchedLocations.Add("[AndroidProvider] PackageManager+SAF");
            yield return location;
        }
    }
}
