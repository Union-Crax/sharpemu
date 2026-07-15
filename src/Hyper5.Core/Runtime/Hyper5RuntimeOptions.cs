// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Hyper5.Core.Runtime;

using Hyper5.Core.Cpu;

public readonly struct Hyper5RuntimeOptions
{
    public CpuExecutionEngine CpuEngine { get; init; }

    public bool StrictDynlibResolution { get; init; }

    public int ImportTraceLimit { get; init; }
}
