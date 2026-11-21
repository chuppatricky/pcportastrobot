using System;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AstroHybrid.App.Models;
using AstroHybrid.App.Services;
using AstroHybrid.Linux.Launch;
using AstroHybrid.Linux.Platform;
using AstroHybrid.Linux.Probe;
using AstroHybrid.Linux.Setup;
using Avalonia.Threading;

using LinuxServices = AstroHybrid.Linux.Platform.Services;

namespace AstroHybrid.App.ViewModels;

public enum LauncherPage
{
    Home,
    GraphicsSettings,
    ControllerSettings,
    AdvancedSettings,
    Installation,
    Compatibility,
}

/// <summary>
/// Linux port of upstream's MainWindowViewModel: the same public members (upstream's page view models
/// are compiled unchanged against it), with the Linux services underneath — the chosen compatibility
/// layer for Play, native setup and repair, the AppImage updater, Vulkan hardware detection with AMD
/// defaults — and awaited dialogs instead of WPF's MessageBox.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private readonly SettingsManager _settingsManager;
    private readonly InstallationManager _installationManager;
    private readonly LinuxHardwareProbe _hardware;
    private readonly GameLauncher _launcher = new();
    private readonly string _rootDirectory;

    public SettingsManager SettingsManager => _settingsManager;

    private LauncherPage _currentPage = LauncherPage.Home;
    private AppSettings _settings = new();
    private InstallationState _installationState;
    private HardwareInfo _hardwareInfo;
    private ObservableCollection<ControllerInfo> _controllers = new();
    private ControllerInfo _selectedController;
    private bool _isLaunching;
    private bool _isLoading;
    private string _statusMessage = "";
    private UpdateState _updateState = new();
    private UpdateOffer _offer;
    private bool _showUpdateNotification;
    private bool _setupRequired;

    public static MainWindowViewModel Current { get; private set; }

    /// <summary>Set by the window: fills in the screen size, which only the UI toolkit knows.</summary>
    public Action<HardwareInfo> ApplyScreen { get; set; }

    /// <summary>The fork's own settings (layer, FSR version, gamemode, ...), saved on every change.</summary>
    public LinuxSettings Linux { get; } = LinuxSettings.Load();

    public CompatibilityViewModel Compatibility { get; }
    public SetupViewModel Setup { get; }

    public MainWindowViewModel(string rootDirectory)
    {
        Current = this;
        _rootDirectory = rootDirectory;
        _settingsManager = new SettingsManager(rootDirectory);
        _installationManager = new InstallationManager(rootDirectory);
        _hardware = (LinuxHardwareProbe)LinuxServices.Hardware;
        Compatibility = new CompatibilityViewModel(this);
        Setup = new SetupViewModel(this);

        NavigateCommand = new RelayCommand<LauncherPage>(page => CurrentPage = page);
        LaunchGameCommand = new RelayCommand(async () => await LaunchGameAsync(), () => !IsLaunching && !IsLoading);
        ApplyBestSettingsCommand = new RelayCommand(ApplyBestSettings);
        OpenSupportCommand = new RelayCommand(() => Dialogs.Open(SupportUrl));
        OpenContributeCommand = new RelayCommand(() => Dialogs.Open(ContributeUrl));
        RefreshControllersCommand = new RelayCommand(RefreshControllers);
        RepairInstallationCommand = new RelayCommand(async () => await RepairInstallationAsync("This will set the game up again from your game dump. Continue?"));
        RebuildInstallationCommand = new RelayCommand(async () => await RepairInstallationAsync("This will completely rebuild the game files from your dump. Continue?"));
        OpenInstallFolderCommand = new RelayCommand(() => Dialogs.Open(AppPaths.RunHybrid));
        OpenLogFolderCommand = new RelayCommand(() => { Directory.CreateDirectory(AppPaths.LogsDir); Dialogs.Open(AppPaths.LogsDir); });
        ExportSupportLogsCommand = new RelayCommand(ExportSupportLogs);
        ChangeInstallLocationCommand = new RelayCommand(() => _ = Dialogs.ShowAsync(
            $"AstroHybrid keeps the game in {AppPaths.DataDir}.\n\nTo use another place, move that folder and start AstroHybrid with ASTROHYBRID_HOME set to the new folder, then use Repair once."));
        UninstallCommand = new RelayCommand(async () => await UninstallAsync());
        CheckForUpdatesCommand = new RelayCommand(async () => await CheckForUpdatesAsync(true));
        DownloadUpdateCommand = new RelayCommand(async () => await DownloadAndInstallUpdateAsync(),
            () => HasUpdateAction && !UpdateState.IsDownloading && !UpdateState.IsInstalling);
        IgnoreUpdateCommand = new RelayCommand(IgnoreUpdate);
        DismissUpdateNotificationCommand = new RelayCommand(() => ShowUpdateNotification = false);
        RestartToUpdateCommand = new RelayCommand(RestartToUpdate, () => UpdateState.IsReadyToRestart);
        StopGameCommand = new RelayCommand(() => GameLauncher.KillGame(), () => IsLaunching);

        _ = InitializeAsync();
    }

    private int _settingsNotifyDepth;

    public override void OnPropertyChanged(string propertyName = null)
    {
        // Upstream's page view models answer a Settings change by refreshing all their properties; an Avalonia
        // ComboBox then gets a fresh label array, writes its (unchanged) selection back, and the page model
        // reports Settings again — endlessly. WPF does not write back there. Inner repeats are not re-raised.
        if (propertyName == nameof(Settings) && _settingsNotifyDepth > 0)
        {
            SaveSettingsQuietly();
            return;
        }
        if (propertyName == nameof(Settings)) _settingsNotifyDepth++;
        try { base.OnPropertyChanged(propertyName); }
        finally { if (propertyName == nameof(Settings)) _settingsNotifyDepth--; }
        if (propertyName == nameof(Settings)) SaveSettingsQuietly();
        CommandRequery.Raise();
    }

    private void SaveSettingsQuietly()
    {
        if (IsLoading || _settingsManager == null) return;
        try { _settingsManager.Save(Settings); } catch (IOException) { }
    }

    public void SaveLinux()
    {
        try { Linux.Save(); } catch (IOException ex) { StatusMessage = $"Could not save settings: {ex.Message}"; }
    }

    public LauncherPage CurrentPage
    {
        get => _currentPage;
        set
        {
            SetProperty(ref _currentPage, value);
            // Upstream's "Run Setup Again" (Installation page) clears FirstRunComplete and goes Home.
            if (value == LauncherPage.Home && !Settings.FirstRunComplete && !IsLoading) SetupRequired = true;
        }
    }
    public AppSettings Settings { get => _settings; set => SetProperty(ref _settings, value); }
    public InstallationState InstallationState { get => _installationState; set => SetProperty(ref _installationState, value); }
    public HardwareInfo HardwareInfo { get => _hardwareInfo; set => SetProperty(ref _hardwareInfo, value); }
    public ObservableCollection<ControllerInfo> Controllers { get => _controllers; set => SetProperty(ref _controllers, value); }
    public ControllerInfo SelectedController { get => _selectedController; set => SetProperty(ref _selectedController, value); }
    public UpdateState UpdateState { get => _updateState; set => SetProperty(ref _updateState, value); }
    public bool ShowUpdateNotification { get => _showUpdateNotification; set => SetProperty(ref _showUpdateNotification, value); }
    public bool IsLaunching { get => _isLaunching; set => SetProperty(ref _isLaunching, value); }
    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    /// <summary>The game is not set up (or the player asked to run the setup again): the window shows the setup page.</summary>
    public bool SetupRequired { get => _setupRequired; set => SetProperty(ref _setupRequired, value); }

    public bool HasUpdateAction => (UpdateState.UpdateAvailable || UpdateState.IgnoredUpdate != null) && !UpdateState.IsReadyToRestart;
    public bool IsUpdateReady => UpdateState.IsReadyToRestart;
    public bool HasIgnoredUpdate => !UpdateState.UpdateAvailable && UpdateState.IgnoredUpdate != null;

    public string AppVersionText => $"AstroHybrid {LinuxServices.Updater.CurrentVersion}";

    /// <summary>The selected card's AMD features (null for other vendors).</summary>
    public AmdFeatures Amd => _hardware.AmdFor(HardwareInfo?.SelectedGpu);

    public ICommand NavigateCommand { get; }
    public ICommand LaunchGameCommand { get; }
    public ICommand StopGameCommand { get; }
    public ICommand ApplyBestSettingsCommand { get; }

    public const string SupportUrl = "https://ko-fi.com/justhappyguy";
    public const string ContributeUrl = "https://github.com/imnotsharingmyname/pcportastrobot";
    public ICommand OpenSupportCommand { get; }
    public ICommand OpenContributeCommand { get; }
    public ICommand RefreshControllersCommand { get; }
    public ICommand RepairInstallationCommand { get; }
    public ICommand RebuildInstallationCommand { get; }
    public ICommand OpenInstallFolderCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand ExportSupportLogsCommand { get; }
    public ICommand ChangeInstallLocationCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand DownloadUpdateCommand { get; }
    public ICommand IgnoreUpdateCommand { get; }
    public ICommand DismissUpdateNotificationCommand { get; }
    public ICommand RestartToUpdateCommand { get; }

    private async Task InitializeAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading...";
        try
        {
            Settings = _settingsManager.Load();
            InstallationState = await _installationManager.GetInstallationStateAsync();
            SetupRequired = InstallationState?.IsInstalled != true;

            var hardware = await Task.Run(() => _hardware.Detect());
            ApplyScreen?.Invoke(hardware);
            HardwareInfo = hardware;
            HardwareInfo.SelectGpu(Settings.Gpu);
            OnPropertyChanged(nameof(HardwareInfo));
            OnPropertyChanged(nameof(Amd));
            await RefreshControllersAsync();

            if (Settings.ApplyBestOnStart && InstallationState?.IsInstalled == true)
            {
                Settings.ApplyBestOnStart = false;
                ApplyBestSettings();
            }
            ResolveAutoGraphics();
            Settings.Sanitize(HardwareInfo.SupportsDlss, HardwareInfo.SupportsFrameGeneration, HardwareInfo.SupportsReflex, HardwareInfo.MaxFrameGenMultiplier);
            _settingsManager.Save(Settings);
            OnPropertyChanged(nameof(Settings));
            StatusMessage = InstallationState?.IsInstalled == true ? "Ready" : "Setup required";

            // As upstream: a game converted earlier for an AMD processor hangs on others; offer to convert it again.
            var features = GameFeatures.Load(AppPaths.RunHybrid);
            if (InstallationState?.IsInstalled == true && features != null && !features.IntelConversion &&
                GameSetup.NeedsIntelConversion() && !string.IsNullOrEmpty(Settings.GameDumpPath) &&
                await Dialogs.ConfirmAsync("This PC's processor needs an updated conversion of the game (it fixes the hang after the title screen on Intel processors).\n\nConvert the game again now? It takes about a minute."))
                await RunRepairAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }

        if (Settings.AutoCheckUpdates && Linux.CheckUpdates)
            _ = CheckForUpdatesLaterAsync();
    }

    // Awaited on the UI thread (the network work inside is asynchronous), so the bound state changes there.
    private async Task CheckForUpdatesLaterAsync()
    {
        await Task.Delay(3000);
        await CheckForUpdatesAsync(false);
    }

    /// <summary>Re-reads the install after the setup page finished.</summary>
    public async Task SetupFinishedAsync()
    {
        Settings = _settingsManager.Load();
        InstallationState = await _installationManager.GetInstallationStateAsync();
        SetupRequired = InstallationState?.IsInstalled != true;
        if (Settings.ApplyBestOnStart && HardwareInfo != null)
        {
            Settings.ApplyBestOnStart = false;
            ApplyBestSettings();
        }
        StatusMessage = SetupRequired ? "Setup required" : "Ready";
        CurrentPage = LauncherPage.Home;
    }

    private async Task LaunchGameAsync()
    {
        if (InstallationState?.IsInstalled != true)
        {
            await Dialogs.ShowAsync("Please complete the setup first.");
            return;
        }
        if (!await EnsureGameLinkAsync()) return;

        IsLaunching = true;
        try
        {
            var progress = new Progress<string>(m => StatusMessage = m);
            var run = await _launcher.LaunchAsync(Settings, HardwareInfo, Linux, progress, CancellationToken.None);
            StatusMessage = $"Astro Bot is starting through {run.Handle.Layer.DisplayName}...";
            _ = ReportVerificationAsync(run);
            var exitCode = await run.Handle.Exited;
            StatusMessage = exitCode == 0 ? "Game exited normally" : $"Game exited with code {exitCode}";
            Compatibility.Refresh();
            if (exitCode != 0)
            {
                var detail = DescribeExitFailure(exitCode);
                if (!string.IsNullOrEmpty(detail)) await Dialogs.ShowAsync(detail, "AstroHybrid - Game Exited With Error");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Launch error: {ex.Message}";
            await Dialogs.ShowAsync($"Failed to launch game:\n{ex.Message}");
        }
        finally
        {
            IsLaunching = false;
        }
    }

    private async Task ReportVerificationAsync(GameRun run)
    {
        try
        {
            var v = await run.Verification;
            Dispatcher.UIThread.Post(() =>
            {
                Compatibility.Refresh();
                if (v.Verified) StatusMessage = $"Running through {run.Handle.Layer.DisplayName} (verified)";
                else if (v.GamePid != 0) _ = Dialogs.ShowAsync(v.Message, "AstroHybrid - Compatibility layer");
            });
        }
        catch (OperationCanceledException) { }
    }

    private void ApplyBestSettings()
    {
        if (HardwareInfo == null) return;
        var best = PickBestSettings(HardwareInfo);
        Settings.Lighting = 0;
        Settings.Fps = 0;
        Settings.Speed = 0;
        Settings.Size = best.Size;
        Settings.Ultrawide = best.Ultrawide;
        Settings.Fullscreen = true;
        Settings.Upscale = best.Upscale;
        Settings.FrameGenMode = best.FrameGen;
        Settings.FrameGenTarget = best.Target;
        Settings.LowLatency = best.LowLatency;
        Settings.UpscaleAuto = true;
        Settings.FrameGenAuto = true;
        Settings.Controller = 0;
        Settings.RumblePercent = 70;
        Settings.Rumble = RumbleSetting.ToLegacyIndex(70);
        if (Amd != null)
        {
            AmdFeatures.ApplyBestSettings(Settings, Linux, Amd);
            SaveLinux();
        }
        _settingsManager.Save(Settings);
        OnPropertyChanged(nameof(Settings));
        StatusMessage = "Applied best settings for this PC";
    }

    public void ResolveAutoGraphics()
    {
        if (HardwareInfo == null) return;
        var best = PickBestSettings(HardwareInfo);
        if (Settings.UpscaleAuto) Settings.Upscale = best.Upscale;
        if (Settings.FrameGenAuto)
        {
            bool wasOff = Settings.FrameGenMode == 0;
            Settings.FrameGenMode = Settings.Upscale != 1 ? 0 : best.FrameGen;
            if (wasOff && Settings.FrameGenMode != 0) Settings.FrameGenTarget = best.Target;
        }
        if (Settings.Upscale != 1) Settings.FrameGenMode = 0;
    }

    public void RestoreGraphicsDefaults()
    {
        if (HardwareInfo == null) return;
        var defaults = new AppSettings();
        Settings.Lighting = defaults.Lighting;
        Settings.Fps = defaults.Fps;
        Settings.Fullscreen = defaults.Fullscreen;
        Settings.VSync = defaults.VSync;
        Settings.Overlay = defaults.Overlay;
        Settings.DlssQuality = defaults.DlssQuality;
        Settings.Gpu = -1;
        HardwareInfo.SelectGpu(-1);
        var best = PickBestSettings(HardwareInfo);
        Settings.Size = best.Size;
        Settings.Ultrawide = best.Ultrawide;
        Settings.UpscaleAuto = true;
        Settings.FrameGenAuto = true;
        Settings.Upscale = best.Upscale;
        Settings.FrameGenMode = best.FrameGen;
        Settings.FrameGenTarget = best.Target;
        Settings.LowLatency = best.LowLatency;
        _settingsManager.Save(Settings);
        StatusMessage = "Graphics settings restored to the defaults for this PC";
        OnPropertyChanged(nameof(HardwareInfo));
        OnPropertyChanged(nameof(Settings));
    }

    internal (int Size, bool Ultrawide, int Upscale, int FrameGen, int Target, bool LowLatency) BestSettingsFor(HardwareInfo pc) => PickBestSettings(pc);

    /// <summary>Upstream's choice (screen size, VRAM limit, RTX features) plus AMD: FSR on Radeon cards,
    /// which upstream leaves on the game's own upscaling.</summary>
    private (int Size, bool Ultrawide, int Upscale, int FrameGen, int Target, bool LowLatency) PickBestSettings(HardwareInfo pc)
    {
        var best = (Size: 2, Ultrawide: false, Upscale: 0, FrameGen: 0, Target: 2, LowLatency: false);
        var displayAspect = pc.ScreenHeight > 0 ? (double)pc.ScreenWidth / pc.ScreenHeight : 16.0 / 9.0;
        if (displayAspect >= 3.2)
        {
            best.Ultrawide = true;
            best.Size = pc.ScreenWidth >= 5120 && pc.ScreenHeight >= 1440 ? 6 : pc.ScreenWidth >= 3840 && pc.ScreenHeight >= 1080 ? 7 : 4;
        }
        else if (displayAspect >= 2.1)
        {
            best.Ultrawide = true;
            best.Size = pc.ScreenWidth >= 5120 && pc.ScreenHeight >= 2160 ? 8 : pc.ScreenWidth >= 3440 && pc.ScreenHeight >= 1440 ? 5 : 4;
        }
        else
        {
            foreach (var i in new[] { 0, 1, 2, 3, 9 })
            {
                var preset = AppSettings.ResolutionPresets[i];
                if (preset.Width <= pc.ScreenWidth && preset.Height <= pc.ScreenHeight) best.Size = i;
            }
        }
        int vramMb = pc.SelectedGpu?.VramMb ?? 0;
        if (vramMb > 0 && vramMb < 11 * 1024)
        {
            if (best.Size == 9) best.Size = 3;
            if (best.Size == 8) best.Size = 5;
        }
        if (pc.RtxSeries >= 20)
        {
            best.Upscale = 1;
            best.LowLatency = true;
        }
        if (pc.RtxSeries >= 40)
        {
            best.FrameGen = 1;
            int goal = pc.RtxSeries >= 50 && pc.RefreshRate >= 120 ? 120 : pc.RtxSeries >= 50 && pc.RefreshRate >= 90 ? 90 : 60;
            best.Target = Array.IndexOf(new[] { 0, 50, 60, 90, 120, 144, 165 }, goal);
            if (best.Target < 0) best.Target = 2;
        }
        if (pc.SelectedGpu?.Vendor == "AMD" && pc.SelectedGpu.IsDiscrete)
            best.Upscale = 2;
        return best;
    }

    private void RefreshControllers() => _ = RefreshControllersAsync();

    private async Task RefreshControllersAsync()
    {
        var detected = await Task.Run(LinuxControllerProbe.Detect);
        Controllers.Clear();
        foreach (var c in detected) Controllers.Add(c);
        OnPropertyChanged(nameof(Controllers));
    }

    /// <summary>A moved dump or Wine-only links in app0 stop the game at start (#44): offer the repair instead.</summary>
    private async Task<bool> EnsureGameLinkAsync()
    {
        string problem;
        try { problem = LinuxSetup.DescribeProblem(); }
        catch (Exception) { return true; }   // never block a launch because the check itself failed
        if (problem == null) return true;

        bool dumpFound = !string.IsNullOrEmpty(Settings.GameDumpPath) && File.Exists(Path.Combine(Settings.GameDumpPath, "eboot.bin"));
        var message = $"Astro Bot can't start: {problem}\n\n" + (dumpFound
            ? "Repair the installation now?"
            : $"The game dump{(string.IsNullOrEmpty(Settings.GameDumpPath) ? "" : $" ({Settings.GameDumpPath})")} was moved, renamed or is on a drive that is not connected. Connect the drive and press Play again, or choose the dump's new folder and repair now?");
        if (!await Dialogs.ConfirmAsync(message)) return false;
        if (!dumpFound && !await PickDumpAsync()) return false;
        await RunRepairAsync();
        return false;
    }

    /// <summary>Asks for the dump folder and saves it; false when cancelled or not an Astro Bot dump.</summary>
    private async Task<bool> PickDumpAsync()
    {
        var folder = await Dialogs.PickFolderAsync("Select your Astro Bot dump folder (the one with eboot.bin)");
        if (folder == null) return false;
        var info = await new GameValidator().ValidateGameDumpAsync(folder);
        if (!info.IsValid)
        {
            await Dialogs.ShowAsync(info.ValidationError ?? "That folder is not an Astro Bot dump.");
            return false;
        }
        if (!string.IsNullOrEmpty(info.Warning)) await Dialogs.ShowAsync(info.Warning, "AstroHybrid - Game version");
        Settings.GameDumpPath = folder;
        _settingsManager.Save(Settings);
        return true;
    }

    private async Task RepairInstallationAsync(string question)
    {
        if (string.IsNullOrEmpty(Settings.GameDumpPath) && !await PickDumpAsync()) return;
        if (!await Dialogs.ConfirmAsync(question, "Repair Installation")) return;
        await RunRepairAsync();
    }

    private async Task RunRepairAsync()
    {
        IsLoading = true;
        StatusMessage = "Setting the game up again...";
        try
        {
            await LinuxSetup.RepairAsync(new Progress<(int Percent, string Message)>(p => StatusMessage = $"{p.Percent}% {p.Message}"));
            Settings = _settingsManager.Load();
            InstallationState = await _installationManager.GetInstallationStateAsync();
            StatusMessage = "Repair complete";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Repair error: {ex.Message}";
            await Dialogs.ShowAsync($"The repair failed:\n{ex.Message}\n\nSee Logs/setup.log.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task UninstallAsync()
    {
        if (!await Dialogs.ConfirmAsync("This will remove the game files AstroHybrid built. Your game dump and your saves will NOT be deleted. Continue?", "Uninstall")) return;
        IsLoading = true;
        StatusMessage = "Uninstalling...";
        try
        {
            await _installationManager.UninstallAsync(true);
            Settings = _settingsManager.Load();
            InstallationState = await _installationManager.GetInstallationStateAsync();
            SetupRequired = true;
            StatusMessage = "Game files removed. Run the setup to install again.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Uninstall error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ---- updates (the AppImage replaces itself; see AppImageUpdater) ----

    private async Task CheckForUpdatesAsync(bool force)
    {
        UpdateState.IsChecking = true;
        PublishUpdateState();
        try
        {
            _offer = await LinuxServices.Updater.CheckAsync(CancellationToken.None);
            var skipped = _offer != null && !force && Linux.SkippedVersion == _offer.Version;
            UpdateState.CurrentVersion = LinuxServices.Updater.CurrentVersion;
            UpdateState.LastCheckTime = DateTime.Now;
            UpdateState.ErrorMessage = null;
            var info = _offer == null ? null : new UpdateInfo { Version = _offer.Version, ReleaseName = _offer.Tag, ReleaseNotes = _offer.Notes, DownloadUrl = _offer.AssetUrl };
            UpdateState.UpdateAvailable = info != null && !skipped;
            UpdateState.LatestUpdate = skipped ? null : info;
            UpdateState.IgnoredUpdate = skipped ? info : null;
            ShowUpdateNotification = UpdateState.UpdateAvailable;
            if (force && _offer == null) StatusMessage = $"AstroHybrid {LinuxServices.Updater.CurrentVersion} is up to date";
        }
        catch (Exception ex)
        {
            UpdateState.ErrorMessage = ex.Message;
            if (force) StatusMessage = $"Update check failed: {ex.Message}";
        }
        finally
        {
            UpdateState.IsChecking = false;
            PublishUpdateState();
        }
    }

    private void PublishUpdateState() => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(UpdateState));
        OnPropertyChanged(nameof(HasUpdateAction));
        OnPropertyChanged(nameof(HasIgnoredUpdate));
        OnPropertyChanged(nameof(IsUpdateReady));
    });

    private async Task DownloadAndInstallUpdateAsync()
    {
        if (_offer == null) return;
        ShowUpdateNotification = true;
        UpdateState.IsDownloading = true;
        UpdateState.DownloadStatus = $"Downloading {_offer.Version}...";
        PublishUpdateState();
        var progress = new Progress<double>(p =>
        {
            UpdateState.DownloadProgress = p;
            UpdateState.DownloadStatus = $"Downloading {_offer.Version}: {p * 100:0}%";
            PublishUpdateState();
        });
        var (ok, message) = await LinuxServices.Updater.InstallAsync(_offer, progress, CancellationToken.None);
        UpdateState.IsDownloading = false;
        UpdateState.IsReadyToRestart = ok;
        UpdateState.DownloadStatus = message;
        UpdateState.ErrorMessage = ok ? null : message;
        PublishUpdateState();
        if (ok)
        {
            Linux.SkippedVersion = "";
            SaveLinux();
            StatusMessage = $"AstroHybrid {_offer.Version} is installed: restart the launcher to use it";
        }
        else if (await Dialogs.ConfirmAsync(message + "\n\nOpen the release page?", "AstroHybrid update", "Open page", "Close") && !string.IsNullOrEmpty(_offer.HtmlUrl))
        {
            Dialogs.Open(_offer.HtmlUrl);
        }
    }

    private void RestartToUpdate()
    {
        if (IsLaunching || GameLauncher.IsGameRunning())
        {
            _ = Dialogs.ShowAsync("Close Astro Bot first, then press Restart launcher to finish the update.");
            return;
        }
        try { LinuxServices.Updater.Relaunch(Array.Empty<string>()); }
        catch (Exception ex) { _ = Dialogs.ShowAsync($"Could not restart: {ex.Message}\n\nClose AstroHybrid and open it again."); }
    }

    private void IgnoreUpdate()
    {
        if (UpdateState.LatestUpdate == null) return;
        Linux.SkippedVersion = UpdateState.LatestUpdate.Version;
        SaveLinux();
        ShowUpdateNotification = false;
        UpdateState.IgnoredVersion = UpdateState.LatestUpdate.Version;
        UpdateState.IgnoredUpdate = UpdateState.LatestUpdate;
        UpdateState.LatestUpdate = null;
        UpdateState.UpdateAvailable = false;
        PublishUpdateState();
    }

    // ---- logs ----

    private string DescribeExitFailure(int exitCode)
    {
        string logSnippet = "";
        try
        {
            foreach (var logName in new[] { "hybrid-run.err.log", "hybrid-run.out.log" })
            {
                var path = Path.Combine(_rootDirectory, "Logs", logName);
                if (!File.Exists(path)) continue;
                var hint = HintFromLog(File.ReadAllText(path), Settings.GameVersion, out bool wholeMessage);
                if (hint == null) continue;
                if (wholeMessage) return hint;
                logSnippet = hint;
                break;
            }
        }
        catch (IOException) { }
        if (string.IsNullOrEmpty(logSnippet)) return "";
        return $"Astro Bot exited with code {exitCode}.\n\n{logSnippet}\n\nLogs are in {AppPaths.LogsDir} (hybrid-run.out.log / hybrid-run.err.log), or use 'Export Support Logs' on the Installation page.";
    }

    // Same advice as upstream's MainWindowViewModel.HintFromLog, with Linux driver wording.
    public static string HintFromLog(string text, string gameVersion, out bool wholeMessage)
    {
        wholeMessage = false;
        bool Has(string s) => text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
        if (Has("VK_EXT_mesh_shader") || Has("meshShader"))
        {
            wholeMessage = true;
            return "Your GPU or Vulkan driver does not support VK_EXT_mesh_shader (required by Astro Bot's mesh shaders).\n\nRX 6000+, RTX 20+ and Intel Arc cards support it: update Mesa (AMD/Intel) or the NVIDIA driver, or select your discrete GPU in Graphics.";
        }
        if (Has("VK_ERROR_DEVICE_LOST") || Has("ErrorDeviceLost"))
            return "The graphics driver reset the GPU (Vulkan device lost). Update Mesa / the GPU driver, close overlays and recording tools, and try a lower resolution. If it keeps happening at the same spot, please report it with the logs.";
        if (Has("ErrorOutOfDeviceMemory") || Has("failed to create image") || Has("allocate sparse image memory failed"))
            return "The GPU ran out of video memory. Lower the resolution, use 'Ray Tracing OFF' (faster lighting), and close other programs that use the GPU. Cards with 8 GB or less are the most affected.";
        var notImplemented = System.Text.RegularExpressions.Regex.Match(text, @"what\(\):\s+(\S[^\r\n]*?) not implemented");
        if (notImplemented.Success)
            return $"The game called a PS5 system function that AstroHybrid does not support yet ({notImplemented.Groups[1].Value}). Please report this with the logs.";
        if (Has("param.json or param.sfo not found"))
            return "The game's files could not be found (param.json). The dump was probably moved, renamed or is on a drive that is not connected. Reconnect it, or use 'Repair Installation' and pick the dump's current folder.";
        if (Has("ObjectDefinitionContext") || Has("Duplicated definition name") || (Has("Xml.cpp:") && Has("is broken and failed to load")))
            return "The game stopped in its asset loader (\"Duplicated definition name\" / \"Xml file is broken\"). " +
                   ((gameVersion ?? "").StartsWith(GameValidator.KnownBadVersion)
                       ? "Your dump is game version 01.007.000, which is known to crash this way. Please update the dump to 01.019.000 and run 'Repair Installation'."
                       : "This is known from game version 01.007.000; 01.019.000 is recommended.");
        return null;
    }

    private void ExportSupportLogs()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            var zipPath = Path.Combine(AppPaths.LogsDir, $"astrohybrid-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var file in new[]
                {
                    Path.Combine(AppPaths.LogsDir, "setup.log"),
                    Path.Combine(AppPaths.LogsDir, "setup-relinker.log"),
                    Path.Combine(AppPaths.LogsDir, "hybrid-run.out.log"),
                    Path.Combine(AppPaths.LogsDir, "hybrid-run.err.log"),
                    Path.Combine(AppPaths.LogsDir, "compat-verify.json"),
                    Path.Combine(_rootDirectory, "Build", "astrohybrid-settings.json"),
                    AppPaths.LinuxSettingsFile,
                    Path.Combine(AppPaths.RunHybrid, "astrohybrid-game.json"),
                })
                    if (File.Exists(file)) archive.CreateEntryFromFile(file, Path.GetFileName(file));
                // The newest Proton log of the last run (which Wine build, which DLLs, what failed).
                var proton = Directory.GetFiles(AppPaths.LogsDir, "steam-*.log").Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                if (proton != null) archive.CreateEntryFromFile(proton.FullName, proton.Name);
            }
            StatusMessage = $"Exported support logs to {Path.GetFileName(zipPath)}";
            _ = Dialogs.ShowAsync($"Support logs bundle created (no game files included):\n{zipPath}", "Export Support Logs");
        }
        catch (Exception ex)
        {
            _ = Dialogs.ShowAsync($"Failed to export logs: {ex.Message}", "Export Support Logs");
        }
    }
}
