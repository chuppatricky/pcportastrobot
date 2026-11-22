using System;
using System.IO;
using AstroHybrid.App.ViewModels;
using AstroHybrid.Linux.Views;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(AstroHybrid.Tests.Ui.TestApp))]

namespace AstroHybrid.Tests.Ui;

public static class TestApp
{
    // Real Skia rendering (not the headless stub), so frames can be captured as images.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<AstroHybrid.Linux.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Opens the real main window on each page. Needs ASTROHYBRID_HOME pointing at a data dir (an installed
/// test home shows every page filled in); with AH_SCREENSHOTS=&lt;dir&gt; each page is saved as a PNG.
/// Without ASTROHYBRID_HOME it is skipped, so a normal test run never touches the real data dir.
/// </summary>
public class ScreenTests
{
    [AvaloniaFact]
    public void EveryPageRenders()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASTROHYBRID_HOME"))) return;
        AstroHybrid.Linux.Platform.Services.Init();
        var vm = new MainWindowViewModel(AstroHybrid.Linux.Platform.AppPaths.DataDir);
        var window = new MainWindow(vm) { Width = 1100, Height = 900 };
        window.Show();
        // Let InitializeAsync (settings, Vulkan, controllers) finish.
        for (var i = 0; i < 200 && vm.IsLoading | vm.HardwareInfo == null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(25);
        }
        var shots = Environment.GetEnvironmentVariable("AH_SCREENSHOTS");
        var settingsFile = vm.SettingsManager.GetSettingsPath();
        var settingsBefore = File.ReadAllText(settingsFile);
        if (vm.SetupRequired) Save(window, shots, "Setup");
        vm.SetupRequired = false;
        foreach (var page in Enum.GetValues<LauncherPage>())
        {
            vm.CurrentPage = page;
            Dispatcher.UIThread.RunJobs();
            Save(window, shots, page.ToString());
        }
        // Only looking at the pages must not change a single setting.
        Assert.Equal(settingsBefore, File.ReadAllText(settingsFile));
        window.Close();
    }

    private static void Save(MainWindow window, string dir, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        frame.Save(Path.Combine(dir, name + ".png"));
    }
}
