using Nyx.App;

namespace Nyx.App.Tests;

[TestClass]
public sealed class SingleInstanceCoordinatorTests
{
    [TestMethod]
    public void SecondaryInstanceForwardsPathsToThePrimary()
    {
        using var receivedSignal = new ManualResetEventSlim();
        using var primary = new SingleInstanceCoordinator();
        Assert.IsTrue(primary.IsPrimary);
        using var secondary = new SingleInstanceCoordinator();
        Assert.IsFalse(secondary.IsPrimary);
        ForwardedLaunch? received = null;
        primary.StartListening(launch =>
        {
            received = launch;
            receivedSignal.Set();
        });
        string[] paths = [@"E:\資料\example.txt"];
        Assert.IsTrue(SingleInstanceCoordinator.ForwardToPrimary(paths));
        Assert.IsTrue(receivedSignal.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsNotNull(received);
        CollectionAssert.AreEqual(paths, received.Paths.ToArray());
    }
}
