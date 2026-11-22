using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AstroHybrid.Linux;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            d.MainWindow = new Views.MainWindow(new AstroHybrid.App.ViewModels.MainWindowViewModel(Platform.AppPaths.DataDir));
        base.OnFrameworkInitializationCompleted();
    }
}
