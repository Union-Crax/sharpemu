// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Hyper5.HLE;

namespace Hyper5.Libs.Kernel;

public static class KernelExceptionCompatExports
{
    private static readonly HashSet<int> AllowedSignals = new() { 1, 4, 8, 10, 11, 30 };
    private static readonly Dictionary<int, ulong> _installedHandlers = new();
    private static readonly object _gate = new();

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
    // (the handler runs before this call returns). The handler's context
    // argument points at a zeroed guest buffer rather than a reconstructed
    // mcontext_t — that layout is unverified, and a guessed struct would
    // corrupt the GC's conservative stack scan; zeros make a wrong read fail
    // loudly instead. Cross-thread delivery (the real stop-the-world) needs
    // scheduler support to run the handler on the TARGET thread and is not
    // implemented yet.
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
            Console.Error.WriteLine(
                $"[LOADER][WARN] sceKernelRaiseException: cross-thread delivery not implemented " +
                $"(target=0x{threadHandle:X16} current=0x{currentThreadHandle:X16} signo={signum} " +
                $"handler=0x{handler:X16}); GC stop-the-world will stall");
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

    private const ulong SignalContextBufferSize = 0x1000;
    private static ulong _signalContextAddress;

    private static bool TryGetSignalContextBuffer(CpuContext ctx, out ulong address)
    {
        lock (_gate)
        {
            if (_signalContextAddress == 0 &&
                !KernelMemoryCompatExports.TryAllocateHleData(
                    ctx, SignalContextBufferSize, 0x1000, out _signalContextAddress))
            {
                address = 0;
                return false;
            }

            address = _signalContextAddress;
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

        return true;
    }
}
