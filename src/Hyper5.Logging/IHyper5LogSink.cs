// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Hyper5.Logging;

public interface IHyper5LogSink
{
    void Write(in LogEntry entry);
}
