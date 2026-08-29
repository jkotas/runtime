// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;

// Regression test for https://github.com/dotnet/runtime/issues/107473
//
// Thread QCalls used to marshal the native Thread* directly. If the managed
// Thread object was finalized (and possibly resurrected) concurrently with a
// QCall in flight, the native Thread could be destroyed while still in use,
// leading to a use-after-free. This test creates many Thread objects, drops
// all managed references to them, forces finalization/resurrection via weak
// references, and then exercises every Thread QCall on each resurrected
// object to make sure none of them crash.
public class Test107473
{
    [Fact]
    public static int TestEntryPoint()
    {
        const int Count = 500;

        var weakThreads = new List<(WeakReference<Thread> weak, bool started)>(Count * 2);

        for (int i = 0; i < Count; i++)
        {
            weakThreads.Add((CreateThread(start: true), true));
            weakThreads.Add((CreateThread(start: false), false));
        }

        int resurrectedCount = 0;
        var exercised = new bool[weakThreads.Count];

        // A single GC.Collect() + WaitForPendingFinalizers() per round is
        // intentional: a second collect in the same round can reclaim
        // objects before we ever observe them as resurrected.
        for (int round = 0; round < 20; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            for (int i = 0; i < weakThreads.Count; i++)
            {
                if (exercised[i])
                {
                    continue;
                }

                (WeakReference<Thread> weak, bool started) = weakThreads[i];
                if (weak.TryGetTarget(out Thread? resurrected))
                {
                    exercised[i] = true;
                    resurrectedCount++;
                    ExerciseThreadQCalls(resurrected, started);
                }
            }
        }

        Assert.True(resurrectedCount > 0, "No thread was ever observed resurrected; the test did not exercise the fix.");

        return 100;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Thread> CreateThread(bool start)
    {
        Thread thread = new(() => { });
        if (start)
        {
            thread.Start();
            thread.Join();
        }

        return new WeakReference<Thread>(thread, trackResurrection: true);
    }

    private static void ExerciseThreadQCalls(Thread thread, bool started)
    {
        Try(() => thread.IsBackground = true);
        Try(() => _ = thread.IsBackground);
        Try(() => thread.Priority = ThreadPriority.Normal);
        Try(() => _ = thread.Priority);
        Try(() => _ = thread.ThreadState);
        Try(() => _ = thread.GetApartmentState());
        Try(() => thread.TrySetApartmentState(ApartmentState.Unknown));
        Try(thread.DisableComObjectEagerCleanup);
        Try(thread.Interrupt);
        Try(() => thread.Join(0));
        Try(() => thread.Name = "resurrected");

        if (!started)
        {
            // Starting a resurrected, never-started thread must not crash;
            // it should fail cleanly because the native Thread is gone.
            Assert.Throws<ThreadStateException>(thread.Start);
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (ThreadStateException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
