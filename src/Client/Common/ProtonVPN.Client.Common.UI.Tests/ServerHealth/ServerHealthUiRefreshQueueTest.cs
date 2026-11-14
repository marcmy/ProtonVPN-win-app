using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerHealthUiRefreshQueueTest
{
    [TestMethod]
    public async Task RapidReplies_AreCoalescedIntoOneUiRefresh()
    {
        TaskCompletionSource dispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<Action> updates = new();
        int refreshed = 0;
        using ServerHealthUiRefreshQueue queue = new(action => { updates.Enqueue(action); dispatched.TrySetResult(); }, () => refreshed++);
        Parallel.For(0, 1000, _ => queue.Request());
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Request(); // Still coalesced until the queued UI update runs.
        Assert.AreEqual(1, updates.Count);
        Assert.IsTrue(updates.TryDequeue(out Action? update));
        update!();
        Assert.AreEqual(1, refreshed);
    }

    [TestMethod]
    public async Task ClosingView_DiscardsAnAlreadyQueuedUiUpdate()
    {
        TaskCompletionSource<Action> dispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int refreshed = 0;
        ServerHealthUiRefreshQueue queue = new(action => dispatched.TrySetResult(action), () => refreshed++);
        queue.Request();
        Action update = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Dispose();
        update();
        queue.Request();
        Assert.AreEqual(0, refreshed);
    }
}
