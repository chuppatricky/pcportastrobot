using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using AstroHybrid.App.Services;
using AstroHybrid.Linux.Platform;
using AstroHybrid.Linux.Setup;

namespace AstroHybrid.App.ViewModels;

/// <summary>
/// First start (or "Run Setup Again"): optionally bring settings and saves over from the older Wine-based
/// AppImage, then set the game up natively from the player's PS5 dump (the dump is only read).
/// </summary>
public sealed class SetupViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private string _dumpPath = "";
    private bool _importLegacy;
    private bool _running;
    private int _percent;
    private string _message = "";

    public SetupViewModel(MainWindowViewModel main)
    {
        _main = main;
        LegacyAvailable = LegacyImport.Available();
        _importLegacy = LegacyAvailable && !main.Linux.LegacyImported;
        if (LegacyAvailable)
        {
            var dry = LegacyImport.Run(dryRun: true);
            if (dry.DumpPath != null && Directory.Exists(dry.DumpPath)) _dumpPath = dry.DumpPath;
        }
        BrowseCommand = new RelayCommand(async () => await BrowseAsync(), () => !Running);
        StartCommand = new RelayCommand(async () => await StartAsync(), () => !Running && !string.IsNullOrWhiteSpace(DumpPath));
        CancelCommand = new RelayCommand(Cancel, () => !Running);
    }

    /// <summary>Back to the launcher without setting up again (only offered when a game is installed).</summary>
    public bool CanCancel => _main.InstallationState?.IsInstalled == true;
    public ICommand CancelCommand { get; }

    private void Cancel()
    {
        _main.Settings.FirstRunComplete = true;
        _main.SettingsManager.Save(_main.Settings);
        _main.SetupRequired = false;
    }

    public bool LegacyAvailable { get; }
    public string LegacyPath => AppPaths.LegacyInstall;
    public bool ImportLegacy { get => _importLegacy; set => SetProperty(ref _importLegacy, value); }
    public string DumpPath { get => _dumpPath; set => SetProperty(ref _dumpPath, value); }
    public bool Running { get => _running; private set => SetProperty(ref _running, value); }
    public int Percent { get => _percent; private set => SetProperty(ref _percent, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string DataDir => AppPaths.DataDir;

    public ICommand BrowseCommand { get; }
    public ICommand StartCommand { get; }

    private async Task BrowseAsync()
    {
        var folder = await Dialogs.PickFolderAsync("Select your Astro Bot dump folder (the one with eboot.bin)");
        if (folder != null) DumpPath = folder;
    }

    private async Task StartAsync()
    {
        var info = await new GameValidator().ValidateGameDumpAsync(DumpPath);
        if (!info.IsValid)
        {
            await Dialogs.ShowAsync(info.ValidationError ?? "That folder is not an Astro Bot dump.");
            return;
        }
        if (!string.IsNullOrEmpty(info.Warning)) await Dialogs.ShowAsync(info.Warning, "AstroHybrid - Game version");

        Running = true;
        try
        {
            if (ImportLegacy)
            {
                Message = "Copying settings and saves from the previous AstroHybrid...";
                await Task.Run(() => LegacyImport.Run(dryRun: false));
            }
            await LinuxSetup.RunAsync(DumpPath, new Progress<(int Percent, string Message)>(p => { Percent = p.Percent; Message = p.Message; }));
            Message = "Done.";
            await _main.SetupFinishedAsync();
        }
        catch (Exception ex)
        {
            Message = $"Setup failed: {ex.Message}";
            await Dialogs.ShowAsync($"The setup failed:\n{ex.Message}\n\nDetails are in {Path.Combine(AppPaths.LogsDir, "setup.log")}.");
        }
        finally
        {
            Running = false;
        }
    }
}
