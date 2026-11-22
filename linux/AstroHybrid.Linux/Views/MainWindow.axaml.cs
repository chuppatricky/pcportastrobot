using System.Collections.Generic;
using System.ComponentModel;
using AstroHybrid.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AstroHybrid.Linux.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private readonly Dictionary<LauncherPage, Control> _pages = new();

    public MainWindow() : this(null) { }

    public MainWindow(MainWindowViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        if (vm == null) return;
        vm.PropertyChanged += OnViewModelChanged;
        // The display the launcher opens on (upstream reads it from Windows): used by Best Settings.
        vm.ApplyScreen = hw =>
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen == null) return;
            hw.ScreenWidth = screen.Bounds.Width;
            hw.ScreenHeight = screen.Bounds.Height;
        };
        // WPF re-checks every command after input (CommandManager); here the window does it.
        AddHandler(PointerReleasedEvent, (_, _) => CommandRequery.Raise(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, _) => CommandRequery.Raise(), RoutingStrategies.Tunnel, handledEventsToo: true);
        ShowPage();
    }

    private void OnViewModelChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.CurrentPage) or nameof(MainWindowViewModel.SetupRequired) or null or "")
            ShowPage();
    }

    /// <summary>One view per page, made on first visit and kept (upstream's page view models subscribe to the
    /// main view model, so they are made once).</summary>
    private void ShowPage()
    {
        if (_vm.SetupRequired)
        {
            PageHost.Content = new SetupView { DataContext = _vm.Setup };
            return;
        }
        var page = _vm.CurrentPage;
        if (!_pages.TryGetValue(page, out var view))
        {
            view = page switch
            {
                LauncherPage.GraphicsSettings => new GraphicsView { DataContext = new GraphicsSettingsViewModel(_vm) },
                LauncherPage.ControllerSettings => new ControllerView { DataContext = new ControllerSettingsViewModel(_vm) },
                LauncherPage.AdvancedSettings => new AdvancedView { DataContext = new AdvancedSettingsViewModel(_vm) },
                LauncherPage.Installation => new InstallationView { DataContext = new InstallationViewModel(_vm) },
                LauncherPage.Compatibility => new CompatibilityView { DataContext = _vm.Compatibility },
                _ => new HomeView { DataContext = new HomeViewModel(_vm) },
            };
            _pages[page] = view;
        }
        if (page == LauncherPage.Compatibility) _vm.Compatibility.Refresh();
        PageHost.Content = view;
        PageScroll.Offset = default;
        foreach (var (button, p) in new (Button, LauncherPage)[]
        {
            (NavHome, LauncherPage.Home), (NavGraphics, LauncherPage.GraphicsSettings), (NavController, LauncherPage.ControllerSettings),
            (NavAdvanced, LauncherPage.AdvancedSettings), (NavCompatibility, LauncherPage.Compatibility), (NavInstallation, LauncherPage.Installation),
        })
            button.Classes.Set("selected", p == page);
        LayerHint.Text = _vm.Compatibility.Selected == null ? "" : $"Runs through {_vm.Compatibility.Selected.Layer.DisplayName}";
    }
}
