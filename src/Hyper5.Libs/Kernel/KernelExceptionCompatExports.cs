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
    // the world restarts.
    //
    // Full delivery requires reconstructing the guest mcontext_t and running the
    // handler on the TARGET thread — the Orbis mcontext layout is unverified, and
    // writing a guessed struct would corrupt the GC's stack scan. Until the layout
    // is confirmed (disassemble the handler this logs), this resolves the call and
    // records the request so a real scheduler-driven delivery can be added without
    // the guest first tripping the unresolved-import sentinel.
    [SysAbiExport(
        Nid = "il03nluKfMk",
        ExportName = "sceKernelRaiseException",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int RaiseException(CpuContext ctx)
    {
        var threadHandle = ctx[CpuRegister.Rdi];
        var signum = unchecked((int)ctx[CpuRegister.Rsi]);

        var hasHandler = TryGetInstalledHandler(signum, out var handler);
        Console.Error.WriteLine(
            $"[LOADER][WARN] sceKernelRaiseException: thread=0x{threadHandle:X16} signo={signum} " +
            (hasHandler
                ? $"handler=0x{handler:X16} (delivery not yet implemented; GC stop-the-world will stall)"
                : "NO handler installed for this signal"));

        if (!AllowedSignals.Contains(signum))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
