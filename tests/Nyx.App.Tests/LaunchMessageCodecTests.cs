using System.Text;
using Nyx.App;

namespace Nyx.App.Tests;

[TestClass]
public sealed class LaunchMessageCodecTests
{
    [TestMethod]
    public async Task RoundTripPreservesUnicodePathsAndEmptyActivation()
    {
        foreach (string[] paths in new[]
        {
            Array.Empty<string>(),
            new[] { @"E:\資料\夜色.txt", @"E:\a folder\name.txt" },
        })
        {
            using var source = new MemoryStream(LaunchMessageCodec.Encode(paths));
            var decoded = await LaunchMessageCodec.ReadAsync(source, CancellationToken.None);
            Assert.IsNotNull(decoded);
            CollectionAssert.AreEqual(paths, decoded.Paths.ToArray());
        }
    }

    [TestMethod]
    [DataRow("{\"Paths\":null}")]
    [DataRow("{\"Paths\":[null]}")]
    [DataRow("{\"Paths\":[\"\"]}")]
    [DataRow("{\"Paths\":[\"   \"]}")]
    [DataRow("{\"Paths\":[\"a\\u0000b\"]}")]
    public async Task RejectsInvalidIncomingPathLists(string json)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => LaunchMessageCodec.ReadAsync(source, CancellationToken.None));
    }

    [TestMethod]
    public async Task RejectsOversizedIncomingMessagesWithoutReadingToEnd()
    {
        using var source = new MemoryStream(new byte[LaunchMessageCodec.MaximumMessageBytes * 2]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => LaunchMessageCodec.ReadAsync(source, CancellationToken.None));
        Assert.IsLessThanOrEqualTo((long)LaunchMessageCodec.MaximumMessageBytes + 16 * 1024, source.Position);
    }

    [TestMethod]
    public void RejectsOversizedOutgoingSelections()
    {
        var paths = Enumerable.Repeat(@"E:\file.txt", LaunchMessageCodec.MaximumPathCount + 1).ToArray();
        Assert.ThrowsExactly<InvalidDataException>(() => LaunchMessageCodec.Encode(paths));
        var expandedJson = Enumerable.Repeat(new string('夜', 2000), 100).ToArray();
        Assert.ThrowsExactly<InvalidDataException>(() => LaunchMessageCodec.Encode(expandedJson));
    }

    [TestMethod]
    public async Task ReadingHonorsCancellation()
    {
        using var source = new MemoryStream(LaunchMessageCodec.Encode([@"E:\file.txt"]));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => LaunchMessageCodec.ReadAsync(source, cancellation.Token));
    }
}
