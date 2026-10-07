using System.Windows.Threading;

namespace Nyx.App.Tests;

[TestClass]
public sealed class CoalescingProgressTests
{
    [STATestMethod]
    public void LargeBurstDeliversOnlyLatestValueOnFlush()
    {
        var received = new List<int>();
        using var progress = new CoalescingProgress<int>(Dispatcher.CurrentDispatcher, received.Add);
        Parallel.For(0, 10_000, progress.Report);
        progress.Report(10_000);
        Assert.IsEmpty(received);
        progress.Flush();
        Assert.HasCount(1, received);
        Assert.AreEqual(10_000, received[0]);
        progress.Flush();
        Assert.HasCount(1, received);
    }

    [STATestMethod]
    public void DisposalFlushesFinalValueAndIgnoresLateReports()
    {
        var received = new List<int>();
        var progress = new CoalescingProgress<int>(Dispatcher.CurrentDispatcher, received.Add);
        progress.Report(42);
        progress.Dispose();
        progress.Report(99);
        progress.Flush();
        progress.Dispose();
        Assert.HasCount(1, received);
        Assert.AreEqual(42, received[0]);
    }
}
