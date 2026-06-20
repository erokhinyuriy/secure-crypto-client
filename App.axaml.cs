using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SecureCryptoClient.ViewModels;
using SecureCryptoClient.Views;

namespace SecureCryptoClient
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainVm = new MainWindowViewModel();
                var mainWindow = new MainWindow
                {
                    DataContext = mainVm,
                };

                // ДИНАМИЧЕСКИЙ РЕcontrol РАЗМЕРОВ ОКНА:
                // Слушаем, какой экран сейчас активен. 
                mainVm.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(mainVm.CurrentContent))
                    {
                        // Если пользователь вошел и перед глазами открывается MainChatViewModel
                        if (mainVm.CurrentContent is MainChatViewModel)
                        {
                            // Разворачиваем до просторных размеров Telegram Desktop
                            mainWindow.Width = 850;
                            mainWindow.Height = 600;
                        }
                        // Если пользователь нажал Выйти и вернулся на AuthViewModel
                        else if (mainVm.CurrentContent is AuthViewModel)
                        {
                            // Сжимаем обратно в аккуратное стартовое окошко
                            mainWindow.Width = 400;
                            mainWindow.Height = 550;
                        }
                    }
                };

                desktop.MainWindow = mainWindow;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}