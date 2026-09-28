using System.Security.Cryptography;
using ValorantMatureContent.Core;

namespace ValorantMatureContent.Core.Tests;

public sealed class DetectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"vmce-unit-{Guid.NewGuid():N}");
    private readonly PhysicalFileSystem fileSystem = new();

    public DetectionTests() => Directory.CreateDirectory(root);

    [Fact]
    public void InstallationRequiresPaksStructure()
    {
        var detector = new InstallationDetector(fileSystem);
        Assert.False(detector.IsValidInstallation(root));

        Directory.CreateDirectory(Path.Combine(root, GameFiles.PaksRelativePath));

        Assert.True(detector.IsValidInstallation(root));
    }

    [Fact]
    public void CompatibilityFailsClosedForUnknownBuild()
    {
        var manifest = new PayloadManifest(1, "test", ["known"], []);
        var service = new CompatibilityService();

        Assert.Equal(CompatibilityStatus.Unknown, service.Check(null, manifest));
        Assert.Equal(CompatibilityStatus.Unsupported, service.Check("other", manifest));
        Assert.Equal(CompatibilityStatus.Supported, service.Check("known", manifest));
    }

    [Theory]
    [InlineData(true, false, MatureContentState.Disabled)]
    [InlineData(true, true, MatureContentState.Conflict)]
    [InlineData(false, false, MatureContentState.Corrupted)]
    public async Task StateUsesCompleteFileSets(bool originals, bool mature, MatureContentState expected)
    {
        var paks = Path.Combine(root, GameFiles.PaksRelativePath);
        Directory.CreateDirectory(paks);
        if (originals)
        {
            WriteSet(paks, GameFiles.Original);
        }

        if (mature)
        {
            WriteSet(paks, GameFiles.Mature);
        }

        var detector = new StateDetector(fileSystem, new Sha256HashService(fileSystem));
        var first = await detector.DetectAsync(root, null, CompatibilityStatus.Unknown);
        var second = await detector.DetectAsync(root, null, CompatibilityStatus.Unknown);

        Assert.Equal(expected, first.State);
        Assert.Equal(first.State, second.State);
        Assert.Equal(first.Files, second.Files);
    }

    [Fact]
    public async Task PartialMatureSetIsDetected()
    {
        var paks = Path.Combine(root, GameFiles.PaksRelativePath);
        Directory.CreateDirectory(paks);
        await File.WriteAllTextAsync(Path.Combine(paks, GameFiles.Mature[0]), "partial");
        var detector = new StateDetector(fileSystem, new Sha256HashService(fileSystem));

        var result = await detector.DetectAsync(root, null, CompatibilityStatus.Unknown);

        Assert.Equal(MatureContentState.Partial, result.State);
    }

    public void Dispose()
    {
        Directory.Delete(root, true);
        GC.SuppressFinalize(this);
    }

    private static void WriteSet(string directory, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(directory, name), name);
        }
    }
}