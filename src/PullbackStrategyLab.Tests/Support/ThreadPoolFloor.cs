using System.Runtime.CompilerServices;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// The pool threads the suite is given before any test runs, so a web test's request is never
/// left queued behind tests that keep every pool thread busy.
///
/// xUnit runs one test per logical processor at a time, on pool threads, and most of this suite
/// is synchronous: a test's constructor migrates a store, and the golden fixture's replay blocks
/// on each async stage in turn. A test server hands a request to the pool, and a pool thread that
/// finishes a test takes its next work from its own queue before the shared one. Above its floor
/// the pool adds threads slowly, and adds one for a stall only when nothing has been taken off any
/// queue for a while, which a suite this busy seldom allows. So a request could wait, with no
/// thread working on it, past the client's hundred-second timeout.
///
/// Found on 2026-09-16, when two full runs each lost a different web test to that timeout. Thread
/// stacks taken while a third run held one for eighty-four seconds showed seventeen or eighteen
/// pool threads, nearly all in store work or the replay, and no thread in any web host's pipeline
/// across forty seconds. Below the floor the pool starts a thread for queued work when it is
/// queued, which is the whole of the fix.
/// </summary>
public static class ThreadPoolFloor
{
    /// <summary>
    /// The pool threads one running test can hold at once: one blocked on an async stage, and one
    /// running that stage.
    /// </summary>
    public const int ThreadsPerTest = 2;

    /// <summary>
    /// The pool threads the runner holds for the whole run: the socket loop to the test platform,
    /// xUnit's execution sink, and the wait on the assembly's run. All three are in the stacks.
    /// </summary>
    public const int RunnerThreads = 3;

    /// <summary>Twice the most the suite can hold at once, so a request queued at that peak still starts.</summary>
    public static int Workers => 2 * ((Environment.ProcessorCount * ThreadsPerTest) + RunnerThreads);

    [ModuleInitializer]
    internal static void Raise()
    {
        ThreadPool.GetMinThreads(out int workers, out int completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, Workers), completionPorts);
    }
}
