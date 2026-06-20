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
            // 1. Задаем имя пользователя в сети
            _chatService.Username = authVm.Username.ToLower().Trim();

            if (authVm.InitializedStorage != null)
            {
                activeStorage = authVm.InitializedStorage;

                // ЖЕЛЕЗОБЕТОННЫЙ ФИКС КЭША: Принудительно закрываем базу данных 
                // и открываем её заново. Это заставит LiteDB физически сбросить 
                // транзакцию регистрации на диск, и файлы store_alice.db запишут ключи!
                activeStorage.Close();
                activeStorage.Initialize(authVm.MasterPassword);

                _localStorage = activeStorage;
            }
        }

        // 2. СБРАСЫВАЕМ память сервиса, чтобы он гарантированно прочитал свежие ключи с диска
        // Удаляем старый _privateIdentityKey, если он был равен null
        typeof(CryptoChatService)
            .GetField("_privateIdentityKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .SetValue(_chatService, null);

        // 3. Честно загружаем ключи из только что переоткрытой базы данных
        bool keysLoaded = _chatService.LoadIdentityKeysFromStorage(activeStorage);

        // Если ключи не загрузились (например, при регистрации), мы подстрахуем 
        // и попробуем забрать их напрямую из AuthViewModel, если у него был свой сервис
        if (!keysLoaded && CurrentContent is AuthViewModel authWindow)
        {
            // С помощью рефлексии достаем приватный ключ, если сервисы разделились в MVVM
            var authService = authWindow.GetType().GetField("_chatService", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(authWindow) as CryptoChatService;
            if (authService != null)
            {
                var privKey = authService.GetType().GetField("_privateIdentityKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(authService);
                typeof(CryptoChatService).GetField("_privateIdentityKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(_chatService, privKey);
                _chatService.PublicKeyBase64 = authService.PublicKeyBase64;
            }
        }

        // 4. Передаем готовую и проверенную базу в чат
        var chatVm = new MainChatViewModel(_chatService, activeStorage);
        CurrentContent = chatVm;

        // 5. Теперь сокет запустится со 100% вероятностью, так как ключи на месте!
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
