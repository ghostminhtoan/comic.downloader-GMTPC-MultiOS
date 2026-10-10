using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ComicDownloaderGMTPC.ViewModels;
using ComicDownloaderGMTPC.Views;

namespace ComicDownloaderGMTPC;

public partial class App : Application
{
    public static MainViewModel SharedMainViewModel { get; } = new MainViewModel();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested += (_, _) =>
            {
                try { SharedMainViewModel.FlushAutoSaveImmediately(); } catch { }
            };
            desktop.Exit += (_, _) =>
            {
                try { SharedMainViewModel.FlushAutoSaveImmediately(); } catch { }
            };
            desktop.MainWindow = new MainWindow
            {
                DataContext = SharedMainViewModel
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            singleViewFactoryApplicationLifetime.MainViewFactory = () => new MainView { DataContext = SharedMainViewModel };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = SharedMainViewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}