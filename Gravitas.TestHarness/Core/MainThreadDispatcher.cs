using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace Gravitas.TestHarness.Core;

/// <summary>Runs work queued from other threads on the Unity main thread (Update).</summary>
public sealed class MainThreadDispatcher : MonoBehaviour
{
    private sealed class Call
    {
        public Func<object> Func;
        public object Result;
        public Exception Error;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }

    private static readonly ConcurrentQueue<Call> Queue = new ConcurrentQueue<Call>();

    internal static MainThreadDispatcher Instance;

    internal static void Install()
    {
        var go = new GameObject("Gravitas.TestHarness.Dispatcher");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<MainThreadDispatcher>();
    }

    private void Update()
    {
        while (Queue.TryDequeue(out var call))
        {
            try { call.Result = call.Func(); }
            catch (Exception e) { call.Error = e; }
            finally { call.Done.Set(); }
        }
    }

    /// <summary>Blocks the calling (non-main) thread until the main thread has run <paramref name="func"/>.</summary>
    public static object Invoke(Func<object> func, int timeoutMs = 15000)
    {
        var call = new Call { Func = func };
        Queue.Enqueue(call);
        if (!call.Done.Wait(timeoutMs))
            throw new TimeoutException("Main thread did not process the request within " + timeoutMs + " ms.");
        if (call.Error != null)
            throw new InvalidOperationException(call.Error.Message, call.Error);
        return call.Result;
    }

    /// <summary>Fire-and-forget version.</summary>
    public static void Post(System.Action action)
    {
        Queue.Enqueue(new Call { Func = () => { action(); return null; } });
    }
}
