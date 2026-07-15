// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Hyper5.HLE;

namespace Hyper5.Libs.Pad;

// Minimal libSceBluetoothHid stub: no host Bluetooth passthrough, so report
// success and let the Pad path provide input (HYPER5_BTHID_UNAVAILABLE=1 fails instead).
public static class BluetoothHidExports
{
    private const int BluetoothHidUnavailable = unchecked((int)0x80960001);

    private static readonly bool _reportUnavailable = string.Equals(
        Environment.GetEnvironmentVariable("HYPER5_BTHID_UNAVAILABLE"),
        "1",
        StringComparison.Ordinal);

    private static int Result(CpuContext ctx) =>
        ctx.SetReturn(_reportUnavailable ? BluetoothHidUnavailable : 0);

    [SysAbiExport(
        Nid = "tul3-GzejQc",
        ExportName = "sceBluetoothHidInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceBluetoothHid")]
    public static int BluetoothHidInit(CpuContext ctx) => Result(ctx);

    [SysAbiExport(
        Nid = "4FUZ+c52d2k",
        ExportName = "sceBluetoothHidRegisterDevice",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceBluetoothHid")]
    public static int BluetoothHidRegisterDevice(CpuContext ctx) => Result(ctx);

    [SysAbiExport(
        Nid = "4Ypfo9RIwfM",
        ExportName = "sceBluetoothHidRegisterCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceBluetoothHid")]
    public static int BluetoothHidRegisterCallback(CpuContext ctx) => Result(ctx);
}
