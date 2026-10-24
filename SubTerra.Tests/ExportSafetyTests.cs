using System.IO;
using SubTerra.Models;
using SubTerra.Services;
using Xunit;

namespace SubTerra.Tests;

public sealed class ExportSafetyTests
{
    [Fact]
    public async Task ReplacementKeepsThePreviousFileIntactForAnOpenReader()
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "scan.ply");
        try
        {
            var storage = new SessionStorage();
            await storage.ExportPlyAsync(path, [new(1, 2, 3, .5f)]);
            var original = await File.ReadAllTextAsync(path);
            using (var previous = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                await storage.ExportPlyAsync(path, [new(4, 5, 6, .8f), new(7, 8, 9, .9f)]);
                using var reader = new StreamReader(previous);
                Assert.Equal(original, await reader.ReadToEndAsync());
            }
            var current = await File.ReadAllTextAsync(path);
            Assert.Contains("element vertex 2", current);
            Assert.Contains("4 5 6 0.8", current);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task LockedDestinationKeepsOriginalAndCleansTemporaryFile()
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "scan.ply");
        try
        {
            const string original = "previous exported data";
            await File.WriteAllTextAsync(path, original);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = await Record.ExceptionAsync(() => new SessionStorage().ExportPlyAsync(path, [new(4, 5, 6, .8f)]));
                Assert.True(error is IOException or UnauthorizedAccessException, $"Expected file-lock failure, got {error}");
            }
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(float.NaN, 0, 0, .8f)]
    [InlineData(0, float.PositiveInfinity, 0, .8f)]
    [InlineData(0, 0, float.NegativeInfinity, .8f)]
    [InlineData(0, 0, 0, float.NaN)]
    [InlineData(0, 0, 0, -.1f)]
    [InlineData(0, 0, 0, 1.1f)]
    public async Task InvalidPointDoesNotReplaceExistingExport(float x, float y, float z, float intensity)
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "scan.ply");
        try
        {
            const string original = "previous exported data";
            await File.WriteAllTextAsync(path, original);
            ScanPoint[] points = [new(1, 2, 3, .5f), new(x, y, z, intensity)];
            await Assert.ThrowsAsync<InvalidDataException>(() => new SessionStorage().ExportPlyAsync(path, points));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string CreateDirectory() => Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"subterra-export-{Guid.NewGuid():N}")).FullName;
}
