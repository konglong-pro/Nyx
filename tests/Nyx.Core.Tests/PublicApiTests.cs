using Nyx.Core.Preflight;

namespace Nyx.Core.Tests;

[TestClass]
public sealed class PublicApiTests
{
    [TestMethod]
    public void CoreAssemblyDoesNotExposeAPublicSdkSurface()
    {
        var publicTypes = typeof(PreflightService).Assembly.ExportedTypes
            .Select(static type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.IsEmpty(
            publicTypes,
            $"Nyx v0.1 has no public SDK. Exported types: {string.Join(", ", publicTypes)}");
    }
}
