// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using Hyper5.Logging;
using System.Reflection;

namespace Hyper5.GUI;

public sealed partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var display = version is not null ? $"v{version.ToString(3)}" : "v0.0.1";
        display += BuildInfo.CommitSha is null
            ? " - dev"
            : BuildInfo.IsOfficialRelease
                ? $" - {BuildInfo.CommitSha}"
                : $" - UNOFFICIAL {BuildInfo.CommitSha}";

        VersionText.Text = display;
        StatusText.Text = "Loading launcher";
    }
}
