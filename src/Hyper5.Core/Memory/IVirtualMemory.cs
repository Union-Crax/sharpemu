// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Hyper5.Core.Loader;
using Hyper5.HLE;

namespace Hyper5.Core.Memory;

public interface IVirtualMemory : ICpuMemory
{
    void Clear();

    void Map(ulong virtualAddress, ulong memorySize, ulong fileOffset, ReadOnlySpan<byte> fileData, ProgramHeaderFlags protection);

    IReadOnlyList<VirtualMemoryRegion> SnapshotRegions();
}
