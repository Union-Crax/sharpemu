// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Hyper5.GUI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();

            base.OnFrameworkInitializationCompleted();

            var openedAt = DateTimeOffset.UtcNow;
            var mainWindow = new MainWindow();
            try
            {
                await mainWindow.InitializeStartupAsync();

                var remaining = TimeSpan.FromMilliseconds(1800) - (DateTimeOffset.UtcNow - openedAt);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining);
                }

                desktop.MainWindow = mainWindow;
                mainWindow.Show();
                splash.Close();
            }
            catch
            {
                splash.Close();
                throw;
            }

            return;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
