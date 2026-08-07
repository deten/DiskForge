using DiskForge.Core.Model;
using DiskForge.Core.Operations;

namespace DiskForge.Engine.Tests;

public class PendingQueueTests
{
    private sealed class FakeOp : IDiskOperation
    {
        private readonly OperationDescriptor _d;
        public FakeOp(string title, bool destructive) => _d = new(title, "", destructive, 0);
        public OperationDescriptor Describe() => _d;
        public DriveCapability RequiredCapabilities() => DriveCapability.None;
        public ValidationResult Validate(SystemState state) => ValidationResult.Ok();
        public SimulationResult Simulate(SystemState state) => new() { Feasible = true };
        public Task<OpResult> ExecuteAsync(IProgress<OpProgress> p, CancellationToken ct) => Task.FromResult(OpResult.Ok(TimeSpan.Zero));
        public Task<VerifyResult> VerifyAsync() => Task.FromResult(VerifyResult.Pass());
    }

    [Fact]
    public void Enqueue_RaisesChanged_AndTracksDestructive()
    {
        var q = new PendingQueue();
        int changed = 0;
        q.Changed += (_, _) => changed++;

        q.Enqueue(new FakeOp("Rename", destructive: false));
        Assert.Single(q);
        Assert.False(q.HasDestructive);

        q.Enqueue(new FakeOp("Format", destructive: true));
        Assert.Equal(2, q.Count);
        Assert.True(q.HasDestructive);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Move_ReordersOperations()
    {
        var q = new PendingQueue();
        var a = new FakeOp("A", false);
        var b = new FakeOp("B", false);
        q.Enqueue(a);
        q.Enqueue(b);

        q.Move(0, 1);

        Assert.Same(b, q.Items[0]);
        Assert.Same(a, q.Items[1]);
    }

    [Fact]
    public void Remove_And_Clear_Work()
    {
        var q = new PendingQueue();
        var a = new FakeOp("A", false);
        q.Enqueue(a);
        Assert.True(q.Remove(a));
        Assert.Empty(q);

        q.Enqueue(new FakeOp("B", true));
        q.Clear();
        Assert.Empty(q);
    }
}
