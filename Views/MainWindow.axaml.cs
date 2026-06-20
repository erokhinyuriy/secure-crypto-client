using Avalonia.Controls;

namespace SecureCryptoClient.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        public void ExpandForChat()
        {
            Width = 850;   // Комфортная ширина для двух колонок в стиле Telegram
            Height = 600;  // Комфортная высота, чтобы сообщения не спрессовывались
            WindowStartupLocation = WindowStartupLocation.CenterScreen; // Центрируем обновленное окно
        }
    }
}