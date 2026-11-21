using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AstroHybrid.Linux;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            d.MainWindow = new Window { Title = "AstroHybrid", Width = 800, Height = 500, Content = "AstroHybrid for Linux" };
        base.OnFrameworkInitializationCompleted();
    }
}
