using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AstroHybrid.Linux.Compat;
using AstroHybrid.Linux.Launch;
using AstroHybrid.Linux.Platform;

using LinuxServices = AstroHybrid.Linux.Platform.Services;

namespace AstroHybrid.App.ViewModels;

/// <summary>One row of the Compatibility page.</summary>
public sealed class LayerItem
{
    public CompatLayer Layer { get; init; }
    public bool IsDefault { get; init; }
    public bool HasPrefix { get; init; }
    public string Name => Layer.DisplayName + (IsDefault ? "  (default)" : "");
    public string Details => $"{(Layer.Kind == CompatKind.Proton ? "Proton" : "Wine")} · {Layer.Version} · {SourceLabel}{(HasPrefix ? " · prefix ready" : "")}";
    public string Warnings => string.Join("\n", Layer.Warnings);
    public bool HasWarnings => Layer.Warnings.Count > 0;
    private string SourceLabel => Layer.Source switch
    {
        "steam-user" => "Steam compatibility tools",
        "steam-system" => "installed system-wide",
        "steam-library" => "Steam library",
        "system" => "system Wine",
        _ => Layer.Source,
    };
}

/// <summary>
/// The Compatibility page: which Wine/Proton build the game runs through. The choice is saved in the
/// Linux settings and used by the next Play; every run is checked (LayerVerifier) and the result shown here.
/// </summary>
public sealed class CompatibilityViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private LayerItem _selected;
    private bool _busy;
    private string _testResult = "";
    private bool _testOk;

    public CompatibilityViewModel(MainWindowViewModel main)
    {
        _main = main;
        RescanCommand = new RelayCommand(Refresh, () => !Busy);
        PreparePrefixCommand = new RelayCommand(async () => await PreparePrefixAsync(), () => !Busy && Selected != null);
        TestCommand = new RelayCommand(async () => await TestAsync(), () => !Busy && Selected != null && !GameLauncher.IsGameRunning());
        OpenPrefixCommand = new RelayCommand(OpenPrefix, () => Selected?.HasPrefix == true);
        ResetPrefixCommand = new RelayCommand(async () => await ResetPrefixAsync(), () => !Busy && Selected?.HasPrefix == true && !GameLauncher.IsGameRunning());
        Refresh();
    }

    public ObservableCollection<LayerItem> Layers { get; } = new();

    public LayerItem Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value) || value == null) return;
            _main.Linux.CompatLayer = value.Layer.Id;
            _main.SaveLinux();
            TestResult = "";
            OnPropertyChanged(nameof(SelectedSummary));
        }
    }

    public string SelectedSummary => Selected == null
        ? "No Wine or Proton was found. Install Proton-CachyOS (pacman -S proton-cachyos) or GE-Proton, or wine."
        : $"Astro Bot will run through {Selected.Layer.DisplayName}.\n{Selected.Layer.Path}\nPrefix: {LinuxServices.Runner.PrefixFor(Selected.Layer)}";

    public bool Busy { get => _busy; private set => SetProperty(ref _busy, value); }
    public string TestResult { get => _testResult; private set => SetProperty(ref _testResult, value); }
    public bool TestOk { get => _testOk; private set => SetProperty(ref _testOk, value); }

    /// <summary>The check of the last real game start.</summary>
    public string LastRun
    {
        get
        {
            var v = LinuxServices.Verifier.LastResult();
            if (v == null) return "The game has not been started through the new launcher yet.";
            var name = Layers.FirstOrDefault(l => l.Layer.Id == v.ExpectedLayerId)?.Layer.DisplayName ?? v.ExpectedLayerId;
            return v.Verified
                ? $"Last start ({v.CheckedAt:g}): verified running through {name}{(v.NtsyncOpen ? ", with ntsync" : "")}.\n{v.ProtonLogHeader}"
                : $"Last start ({v.CheckedAt:g}): {v.Message}";
        }
    }

    public bool LastRunOk => LinuxServices.Verifier.LastResult()?.Verified != false;

    public bool GameMode { get => _main.Linux.GameMode; set { _main.Linux.GameMode = value; _main.SaveLinux(); OnPropertyChanged(); } }
    public bool MangoHud { get => _main.Linux.MangoHud; set { _main.Linux.MangoHud = value; _main.SaveLinux(); OnPropertyChanged(); } }
    public bool GameModeInstalled => CompatRunner.Which("gamemoderun") != null;
    public string ExtraEnv { get => _main.Linux.ExtraEnv; set { _main.Linux.ExtraEnv = value ?? ""; _main.SaveLinux(); OnPropertyChanged(); } }

    public ICommand RescanCommand { get; }
    public ICommand PreparePrefixCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand OpenPrefixCommand { get; }
    public ICommand ResetPrefixCommand { get; }

    public void Refresh()
    {
        var layers = LinuxServices.Catalog.Discover();
        var def = LayerCatalog.PickDefault(layers);
        var chosen = layers.FirstOrDefault(l => l.Id == _main.Linux.CompatLayer) ?? def;
        Layers.Clear();
        foreach (var l in layers)
            Layers.Add(new LayerItem
            {
                Layer = l,
                IsDefault = l == def,
                HasPrefix = Directory.Exists(LinuxServices.Runner.PrefixFor(l)),
            });
        _selected = Layers.FirstOrDefault(i => i.Layer == chosen);
        OnPropertyChanged(string.Empty);
    }

    private async Task PreparePrefixAsync()
    {
        Busy = true;
        try
        {
            await LinuxServices.Runner.EnsurePrefixAsync(Selected.Layer, new Progress<string>(m => TestResult = m), CancellationToken.None);
            TestOk = true;
        }
        catch (Exception ex)
        {
            TestResult = ex.Message;
            TestOk = false;
        }
        finally
        {
            Busy = false;
            KeepSelectionAndRefresh();
        }
    }

    /// <summary>Runs a small Windows program (Wine's own ping.exe) through the layer and checks which
    /// Wine build it really ran on, the same check every game start gets.</summary>
    private async Task TestAsync()
    {
        Busy = true;
        TestResult = $"Testing {Selected.Layer.DisplayName}...";
        try
        {
            var layer = Selected.Layer;
            await LinuxServices.Runner.EnsurePrefixAsync(layer, new Progress<string>(m => TestResult = m), CancellationToken.None);
            var handle = await LinuxServices.Runner.StartAsync(new RunRequest
            {
                Layer = layer,
                ExePath = CompatRunner.BuiltinExe(layer, "ping.exe"),
                Args = new[] { "-n", "4", "127.0.0.1" },
                WorkingDirectory = AppPaths.DataDir,
                StdoutLog = Path.Combine(AppPaths.LogsDir, "compat-test.log"),
                StderrLog = Path.Combine(AppPaths.LogsDir, "compat-test.log"),
            }, CancellationToken.None);
            var v = await LinuxServices.Verifier.VerifyAsync(handle, TimeSpan.FromSeconds(90), CancellationToken.None);
            await handle.Exited;
            TestOk = v.Verified;
            TestResult = v.Verified
                ? $"Works: a test program ran through {layer.DisplayName} (checked by its Wine library){(v.NtsyncOpen ? ", with ntsync" : "")}."
                : v.Message;
        }
        catch (Exception ex)
        {
            TestOk = false;
            TestResult = ex.Message;
        }
        finally
        {
            Busy = false;
            KeepSelectionAndRefresh();
        }
    }

    private void OpenPrefix() => Dialogs.Open(LinuxServices.Runner.PrefixFor(Selected.Layer));

    /// <summary>Deletes the layer's Wine prefix (rebuilt on the next start). Saves live in Build/RunHybrid, not in prefixes.</summary>
    private async Task ResetPrefixAsync()
    {
        var prefix = LinuxServices.Runner.PrefixFor(Selected.Layer);
        if (!await Dialogs.ConfirmAsync($"Delete the {Selected.Layer.DisplayName} prefix?\n{prefix}\n\nIt is rebuilt on the next start. Your saves are not in it and stay as they are.", "Reset prefix", "Delete", "Cancel"))
            return;
        Busy = true;
        try
        {
            await Task.Run(() => Directory.Delete(prefix, recursive: true));
            TestResult = "Prefix deleted; it is created again on the next start.";
        }
        catch (Exception ex)
        {
            TestResult = $"Could not delete the prefix: {ex.Message}";
        }
        finally
        {
            Busy = false;
            KeepSelectionAndRefresh();
        }
    }

    private void KeepSelectionAndRefresh()
    {
        var keep = _main.Linux.CompatLayer;
        Refresh();
        _main.Linux.CompatLayer = keep;
    }
}
