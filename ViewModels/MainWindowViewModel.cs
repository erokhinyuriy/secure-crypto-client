using System.ComponentModel;
using System.Runtime.CompilerServices;
using SecureCryptoClient.Services;

namespace SecureCryptoClient.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
{
    // Общие сервисы на всё приложение (синглтоны)
    private readonly CryptoChatService _chatService;
    private LocalSecureStorage _localStorage;

    private object _currentContent;

    // Свойство, к которому привязан ContentControl в XAML.
    // Меняя этот объект, мы меняем экран для пользователя!
    public object CurrentContent
    {
        get => _currentContent;
        set { _currentContent = value; OnPropertyChanged(); }
    }

    public MainWindowViewModel()
    {
        // 1. Создаем один экземпляр сервиса сети/криптографии.
        // Передаем временный ник "user" (при регистрации в AuthViewModel он перепишется)
        _chatService = new CryptoChatService("user");
        _localStorage = new LocalSecureStorage("user");

        // 2. При старте создаем AuthViewModel и подписываемся на успешный вход
        var authVm = new AuthViewModel(_chatService, _localStorage, OnAuthSuccess);

        // 3. Устанавливаем экран авторизации как стартовый
        _currentContent = authVm;
    }

    // Этот метод сработает, когда в AuthViewModel выполнится вход или регистрация
    private async void OnAuthSuccess()
    {
        // По умолчанию берем базовый объект, если что-то пойдет не так
        LocalSecureStorage activeStorage = _localStorage;

        if (CurrentContent is AuthViewModel authVm)
        {
            // 1. Принудительно задаем имя пользователя в сети
            _chatService.Username = authVm.Username.ToLower().Trim();

            if (authVm.InitializedStorage != null)
            {
                activeStorage = authVm.InitializedStorage;
                _localStorage = activeStorage;
            }

            // 2. КРИТИЧЕСКИЙ ФИКС: Загружаем или сохраняем ключи Ed25519 в зашифрованную базу!
            // Теперь ключи привязываются к вашему профилю навсегда
            _chatService.LoadIdentityKeysFromStorage(activeStorage);
        }

        // 3. Передаем готовую базу в чат
        var chatVm = new MainChatViewModel(_chatService, activeStorage);
        CurrentContent = chatVm;

        // 4. Подключаем сокет (подписи теперь будут сотыми долями процента совпадать с сервером!)
        await chatVm.InitializeAndConnectAsync();
    }

    #region INotifyPropertyChanged
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    #endregion
}
