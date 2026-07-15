// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Hyper5.HLE;

public interface ICpuMemory
{
    bool TryRead(ulong virtualAddress, Span<byte> destination);

    bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source);

    bool TryCompare(ulong virtualAddress, ReadOnlySpan<byte> expected) => false;
}
