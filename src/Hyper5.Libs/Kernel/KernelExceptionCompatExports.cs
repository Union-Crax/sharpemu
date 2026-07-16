// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using Hyper5.HLE;

namespace Hyper5.Libs.Kernel;

public static class KernelExceptionCompatExports
{
    private static readonly HashSet<int> AllowedSignals = new() { 1, 4, 8, 10, 11, 30 };
    private static readonly Dictionary<int, ulong> _installedHandlers = new();
    private static readonly object _gate = new();

    // Signals raised at a thread other than the caller, keyed by the target's
    // pthread handle. They run on the TARGET thread the next time it reaches a
    // safe delivery point — the poll loops of blocking HLE waits call
    // TryDeliverPendingSignals — so scePthreadSelf and guest TLS inside the
    // handler match the thread the signal was aimed at.
    private static readonly ConcurrentDictionary<ulong, ConcurrentQueue<int>> _pendingSignals = new();
    private static int _pendingSignalCount;

    static KernelExceptionCompatExports()
    {
        GuestThreadExecution.PendingThreadInterruptHandler = TryDeliverPendingSignals;
    }

    [SysAbiExport(
        Nid = "WkwEd3N7w0Y",
        ExportName = "sceKernelInstallExceptionHandler",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int InstallExceptionHandler(CpuContext ctx)
    {
        var signum = unchecked((int)ctx[CpuRegister.Rdi]);
        var handler = ctx[CpuRegister.Rsi];

        if (!AllowedSignals.Contains(signum))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_gate)
        {
            if (_installedHandlers.ContainsKey(signum))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_ALREADY_EXISTS;
            }

            _installedHandlers[signum] = handler;
        }

        Console.Error.WriteLine(
            $"[LOADER][INFO] sceKernelInstallExceptionHandler: signo={signum} handler=0x{handler:X16}");

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Qhv5ARAoOEc",
        ExportName = "sceKernelRemoveExceptionHandler",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int RemoveExceptionHandler(CpuContext ctx)
    {
        var signum = unchecked((int)ctx[CpuRegister.Rdi]);

        if (!AllowedSignals.Contains(signum))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_gate)
        {
            _installedHandlers.Remove(signum);
        }

        Console.Error.WriteLine($"[LOADER][INFO] sceKernelRemoveExceptionHandler: signo={signum}");

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryGetInstalledHandler(int signum, out ulong handler)
    {
        lock (_gate)
        {
            return _installedHandlers.TryGetValue(signum, out handler);
        }
    }

    // Unity/il2cpp's Boehm GC stop-the-world: the collector raises signal 30 on
    // each GC-registered thread, whose installed handler saves its register
    // context (for conservative stack scanning), acknowledges, and parks until
    // the world restarts. Before enabling that machinery, Unity self-tests
    // delivery at startup: it raises the signal on the CALLING thread and then
    // waits on a condvar its handler is expected to signal.
    //
    // Self-raise is delivered synchronously here, matching raise() semantics
    // (the handler runs before this call returns). A raise at ANOTHER thread
    // is queued and runs on the target thread at its next wait-poll safe
    // point (see TryDeliverPendingSignals). In both cases the handler's
    // context argument points at a zeroed guest buffer rather than a
    // Orbis ucontext_t populated from the interrupted guest registers. Boehm
    // reads its stack pointer and callee-saved registers for conservative
    // scanning after the suspend handler acknowledges.
    [SysAbiExport(
        Nid = "il03nluKfMk",
        ExportName = "sceKernelRaiseException",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int RaiseException(CpuContext ctx)
    {
        var threadHandle = ctx[CpuRegister.Rdi];
        var signum = unchecked((int)ctx[CpuRegister.Rsi]);

        if (!AllowedSignals.Contains(signum))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryGetInstalledHandler(signum, out var handler) || handler == 0)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] sceKernelRaiseException: thread=0x{threadHandle:X16} signo={signum} " +
                "has no installed handler; dropping");
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }

        var currentThreadHandle = KernelPthreadState.GetCurrentThreadHandle();
        if (threadHandle != currentThreadHandle)
        {
            var queue = _pendingSignals.GetOrAdd(threadHandle, _ => new ConcurrentQueue<int>());
            queue.Enqueue(signum);
            Interlocked.Increment(ref _pendingSignalCount);

            // A host-owned target (e.g. the main thread) picks the signal up in
            // its wait-poll loop. A scheduler-owned target is explicitly made
            // runnable without completing the wait it was parked in.
            var targetIsSchedulerThread = false;
            var interruptWakeQueued = false;
            if (GuestThreadExecution.Scheduler is { } snapshotScheduler)
            {
                foreach (var snapshot in snapshotScheduler.SnapshotThreads())
                {
                    if (snapshot.ThreadHandle == threadHandle)
                    {
                        targetIsSchedulerThread = true;
                        interruptWakeQueued = snapshotScheduler.TryWakeThreadForInterrupt(threadHandle);
                        break;
                    }
                }
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] sceKernelRaiseException: queued signo={signum} for " +
                $"thread=0x{threadHandle:X16} (from=0x{currentThreadHandle:X16} handler=0x{handler:X16})" +
                (targetIsSchedulerThread
                    ? interruptWakeQueued
                        ? "; scheduler guest thread scheduled for interrupt delivery"
                        : "; scheduler guest thread was not parked; delivery queued for its next safe point"
                    : "; target delivers at its next wait poll"));
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }

        if (GuestThreadExecution.Scheduler is not { } scheduler ||
            !TryGetSignalContextBuffer(ctx, out var contextAddress))
        {
            Console.Error.WriteLine(
                $"[LOADER][ERROR] sceKernelRaiseException: cannot deliver signo={signum} to self " +
                "(no scheduler or context buffer)");
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }

        if (!scheduler.TryCallGuestFunction(
                ctx,
                handler,
                unchecked((ulong)signum),
                contextAddress,
                0,
                0,
                "sceKernelRaiseException",
                out var error))
        {
            Console.Error.WriteLine(
                $"[LOADER][ERROR] sceKernelRaiseException: handler 0x{handler:X16} failed for " +
                $"signo={signum}: {error}");
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }

        Console.Error.WriteLine(
            $"[LOADER][INFO] sceKernelRaiseException: delivered signo={signum} to self " +
            $"(thread=0x{threadHandle:X16}) via handler 0x{handler:X16}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // Runs any signals queued for the CURRENT thread by a cross-thread
    // RaiseException. Called from the poll loops of blocking HLE waits (cond
    // wait, host-thread sema wait), which is a safe point: the guest stack is
    // quiescent, no HLE locks are held, and the handler runs on the thread the
    // signal targeted. The handler may itself block (Boehm's stop-the-world
    // handler acks and then parks until the world restarts) — that wait pumps
    // and re-enters this method, which lets a subsequent restart signal for
    // this thread deliver nested, matching sigsuspend-based signal semantics.
    internal static bool TryDeliverPendingSignals(CpuContext ctx)
    {
        if (Volatile.Read(ref _pendingSignalCount) == 0)
        {
            return false;
        }

        var threadHandle = KernelPthreadState.GetCurrentThreadHandle();
        if (!_pendingSignals.TryGetValue(threadHandle, out var queue))
        {
            return false;
        }

        var delivered = false;
        while (queue.TryDequeue(out var signum))
        {
            Interlocked.Decrement(ref _pendingSignalCount);
            if (!TryGetInstalledHandler(signum, out var handler) || handler == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] sceKernelRaiseException: pending signo={signum} for " +
                    $"thread=0x{threadHandle:X16} no longer has a handler; dropping");
                continue;
            }

            if (GuestThreadExecution.Scheduler is not { } scheduler ||
                !TryGetSignalContextBuffer(ctx, out var contextAddress))
            {
                Console.Error.WriteLine(
                    $"[LOADER][ERROR] sceKernelRaiseException: cannot deliver pending signo={signum} " +
                    $"to thread=0x{threadHandle:X16} (no scheduler or context buffer)");
                continue;
            }

            if (!scheduler.TryCallGuestFunction(
                    ctx,
                    handler,
                    unchecked((ulong)signum),
                    contextAddress,
                    0,
                    0,
                    "sceKernelRaiseException.pending",
                    out var error))
            {
                Console.Error.WriteLine(
                    $"[LOADER][ERROR] sceKernelRaiseException: pending handler 0x{handler:X16} failed " +
                    $"for signo={signum} on thread=0x{threadHandle:X16}: {error}");
                continue;
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] sceKernelRaiseException: delivered pending signo={signum} on " +
                $"thread=0x{threadHandle:X16} via handler 0x{handler:X16}");
            delivered = true;
        }

        return delivered;
    }

    private const ulong SignalContextBufferSize = 0x1000;
    private const ulong UcontextMcontextOffset = 0x40;
    private const ulong McontextLength = 0x480;
    private static readonly ConditionalWeakTable<ICpuMemory, ConcurrentDictionary<ulong, ulong>>
        _signalContextAddresses = new();

    private static bool TryGetSignalContextBuffer(CpuContext ctx, out ulong address)
    {
        var threadHandle = KernelPthreadState.GetCurrentThreadHandle();
        var addresses = _signalContextAddresses.GetOrCreateValue(ctx.Memory);
        lock (_gate)
        {
            if (!addresses.TryGetValue(threadHandle, out address) &&
                (!KernelMemoryCompatExports.TryAllocateHleData(
                    ctx, SignalContextBufferSize, 0x1000, out address) ||
                 !addresses.TryAdd(threadHandle, address)))
            {
                address = 0;
                return false;
            }
        }

        // Re-zero between deliveries so a handler never sees a previous
        // signal's scribbles.
        Span<byte> zeroes = stackalloc byte[512];
        zeroes.Clear();
        for (ulong offset = 0; offset < SignalContextBufferSize; offset += (ulong)zeroes.Length)
        {
            if (!ctx.Memory.TryWrite(address + offset, zeroes))
            {
                return false;
            }
        }

        return TryPopulateSignalUcontext(ctx, address);
    }

    // PS4/PS5 use the FreeBSD-derived amd64 ucontext ABI. The first 0x40
    // bytes are uc_sigmask plus reserved fields; uc_mcontext follows. Keep
    // these offsets in sync with Orbis Mcontext/Ucontext.
    private static bool TryPopulateSignalUcontext(CpuContext ctx, ulong address)
    {
        var mc = address + UcontextMcontextOffset;
        return
            Write64(0x08, ctx[CpuRegister.Rdi]) &&
            Write64(0x10, ctx[CpuRegister.Rsi]) &&
            Write64(0x18, ctx[CpuRegister.Rdx]) &&
            Write64(0x20, ctx[CpuRegister.Rcx]) &&
            Write64(0x28, ctx[CpuRegister.R8]) &&
            Write64(0x30, ctx[CpuRegister.R9]) &&
            Write64(0x38, ctx[CpuRegister.Rax]) &&
            Write64(0x40, ctx[CpuRegister.Rbx]) &&
            Write64(0x48, ctx[CpuRegister.Rbp]) &&
            Write64(0x50, ctx[CpuRegister.R10]) &&
            Write64(0x58, ctx[CpuRegister.R11]) &&
            Write64(0x60, ctx[CpuRegister.R12]) &&
            Write64(0x68, ctx[CpuRegister.R13]) &&
            Write64(0x70, ctx[CpuRegister.R14]) &&
            Write64(0x78, ctx[CpuRegister.R15]) &&
            Write64(0xA0, ctx.Rip) &&
            Write64(0xB0, ctx.Rflags == 0 ? 0x202UL : ctx.Rflags) &&
            Write64(0xB8, ctx[CpuRegister.Rsp]) &&
            Write64(0xC8, McontextLength) &&
            Write64(0x440, ctx.FsBase) &&
            Write64(0x448, ctx.GsBase);

        bool Write64(ulong offset, ulong value) => ctx.TryWriteUInt64(mc + offset, value);
    }
}
