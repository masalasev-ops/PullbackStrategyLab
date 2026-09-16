using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The floor under the pool is in force for the whole suite, so a web test's request cannot be
/// starved behind the synchronous tests running beside it.
///
/// This is the assertion that fails when the initializer is removed. The starvation itself is not
/// asserted: whether it happens depends on what else a run is doing at that moment, and a test that
/// waited for it would be slow and would still pass on a quiet machine.
/// </summary>
public sealed class ThreadPoolFloorTests
{
    [Fact]
    public void The_pool_starts_threads_up_to_the_floor_before_any_test_runs()
    {
        ThreadPool.GetMinThreads(out int workers, out _);

        Assert.True(
            workers >= ThreadPoolFloor.Workers,
            $"The pool's floor is {workers} worker threads, below the {ThreadPoolFloor.Workers} the suite needs.");
    }
}
