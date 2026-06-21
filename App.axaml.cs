using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
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

                // ЖЕЛЕЗОБЕТОННЫЙ ХУК УВЕДОМЛЕНИЙ:
                // Создаем менеджер всплывающих окон и жестко привязываем его к нашему MainWindow.
                // Параметр Position.BottomRight заставит карточки всплывать в правом нижнем углу экрана!
                var notificationManager = new WindowNotificationManager(mainWindow)
                {
                    Position = NotificationPosition.BottomRight,
                    MaxItems = 4 // Максимум 4 уведомления одновременно друг над другом, как в Telegram
                };

                // Передаем ссылку на менеджер в наш синглтон CryptoChatService, 
                // чтобы сетевой слой мог триггерить всплывашки при получении байт из сокета
                mainVm.ChatService.SetNotificationManager(notificationManager);

                // Слушаем, какой экран сейчас активен (переключение размеров окна)
                mainVm.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(mainVm.CurrentContent))
                    {
                        if (mainVm.CurrentContent is MainChatViewModel)
                        {
                            mainWindow.Width = 850;
                            mainWindow.Height = 600;
                        }
                        else if (mainVm.CurrentContent is AuthViewModel)
                        {
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