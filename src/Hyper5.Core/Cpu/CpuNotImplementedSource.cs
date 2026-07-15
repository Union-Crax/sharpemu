// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Hyper5.Core.Cpu;

public enum CpuNotImplementedSource
{
    Unknown = 0,

    InstructionBudget = 1,

    KernelDynlibDlsym = 2,

    HleExport = 3,

    NativeBackend = 4,
}
