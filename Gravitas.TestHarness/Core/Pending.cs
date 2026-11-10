using System;
using System.Threading;

namespace Gravitas.TestHarness.Core;

/// <summary>
/// Returned by a handler whose answer is not known yet (e.g. /time/advance). The HTTP worker thread
/// waits for <see cref="Complete"/> / <see cref="Fail"/>; the main thread keeps running meanwhile.
/// </summary>
public sealed class Pending
{
    internal readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    internal object Result;
    internal Exception Error;

    public int TimeoutMs = 120000;

    /// <summary>Runs on the main thread if the wait timed out, to abort the operation.</summary>
    public System.Action OnTimeout;

    public void Complete(object result)
    {
        Result = result;
        Done.Set();
    }

    public void Fail(Exception error)
    {
        Error = error;
        Done.Set();
    }
}
