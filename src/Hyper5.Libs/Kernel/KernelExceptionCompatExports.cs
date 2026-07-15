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
}
