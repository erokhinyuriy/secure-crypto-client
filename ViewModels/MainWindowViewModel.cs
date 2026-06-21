using System.ComponentModel;
using System.Runtime.CompilerServices;
using SecureCryptoClient.Services;

namespace SecureCryptoClient.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
{
    // Общие сервисы на всё приложение (синглтоны)
    private readonly CryptoChatService _chatService;
    private LocalSecureStorage _localStorage;

    public CryptoChatService ChatService { get => _chatService;  }

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
        _chatService = new CryptoChatService("user");
        _localStorage = new LocalSecureStorage("user");

        var authVm = new AuthViewModel(_chatService, _localStorage, OnAuthSuccess);
        _currentContent = authVm;

        // ИСПРАВЛЕНО: Программа сама узнает из settings.json, кто заходил последним!
        string? lastUser = LocalSecureStorage.GetLastUser();

        if (!string.IsNullOrEmpty(lastUser))
        {
            // Подставляем имя последнего пользователя в текстовое поле формы, 
            // чтобы даже если автологин выключен, пользователю не нужно было вводить ник заново
            authVm.Username = lastUser;

            // Запускаем динамическую проверку пароля для этого конкретного юзера
            TriggerAutoLoginIfPossible(lastUser);
        }
    }

    // Этот метод сработает, когда в AuthViewModel выполнится вход или регистрация
    private async void OnAuthSuccess()
    {
        // По умолчанию берем базовый объект, если что-то пойдет не так
        LocalSecureStorage activeStorage = _localStorage;

        if (CurrentContent is AuthViewModel authVm)
        {
            _chatService.Username = authVm.Username.ToLower().Trim();

            if (authVm.InitializedStorage != null)
            {
                activeStorage = authVm.InitializedStorage;
                activeStorage.Close();
                activeStorage.Initialize(authVm.MasterPassword);
                _localStorage = activeStorage;
            }
        }

        _chatService.LoadIdentityKeysFromStorage(activeStorage);

        var chatVm = new MainChatViewModel(_chatService, activeStorage);

        // --- ПУНКТ 2: ПОДПИСЫВАЕМСЯ НА КНОПКУ ВЫХОДА ---
        chatVm.LogoutRequested += () =>
        {
            // При выходе создаем чистую AuthViewModel и возвращаем пользователя на экран входа
            var newAuthVm = new AuthViewModel(_chatService, _localStorage, OnAuthSuccess);

            // Подставляем имя последнего юзера, чтобы не вводить заново
            string? lastUser = LocalSecureStorage.GetLastUser();
            if (!string.IsNullOrEmpty(lastUser)) newAuthVm.Username = lastUser;

            CurrentContent = newAuthVm;
        };
        // -----------------------------------------------

        CurrentContent = chatVm;
        await chatVm.InitializeAndConnectAsync();
    }

    private void TriggerAutoLoginIfPossible(string username)
    {
        // Ищем сохраненный пароль в кроссплатформенном AES-сейфе, привязанном к железу
        string? savedPassword = LocalSecureStorage.TryGetSavedPassword(username);

        if (!string.IsNullOrEmpty(savedPassword) && _currentContent is AuthViewModel authVm)
        {
            authVm.MasterPassword = savedPassword;
            authVm.LoginLocal();
        }
    }

    #region INotifyPropertyChanged
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    #endregion
}
