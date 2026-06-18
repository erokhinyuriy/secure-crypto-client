using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using SecureCryptoClient.Services;

namespace SecureCryptoClient.ViewModels;

public class AuthViewModel : INotifyPropertyChanged
{
    private readonly CryptoChatService _chatService;
    private LocalSecureStorage _localStorage;
    private readonly Action _onAuthSuccess;
    public LocalSecureStorage? InitializedStorage { get; private set; }

    private string _username = "";
    private string _masterPassword = "";
    private string _statusMessage = "Введите мастер-пароль для входа.";
    private bool _isButtonsEnabled = true;

    // Свойства для привязки к XAML-полям (Data Binding)
    public string Username
    {
        get => _username;
        set { _username = value; OnPropertyChanged(); }
    }

    public string MasterPassword
    {
        get => _masterPassword;
        set { _masterPassword = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool IsButtonsEnabled
    {
        get => _isButtonsEnabled;
        set { _isButtonsEnabled = value; OnPropertyChanged(); }
    }

    public AuthViewModel(CryptoChatService chatService, LocalSecureStorage localStorage, Action onAuthSuccess)
    {
        _chatService = chatService;
        _localStorage = localStorage;
        _onAuthSuccess = onAuthSuccess;
    }

    // Логика кнопки "ЗАРЕГИСТРИРОВАТЬСЯ"
    public async Task RegisterAndConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(MasterPassword))
        {
            StatusMessage = "Заполните все поля!";
            return;
        }

        IsButtonsEnabled = false;
        StatusMessage = "Регистрация ключей устройства на сервере...";

        // ИСПРАВЛЕНО: Создаем и инициализируем базу строго под введённым ником!
        var storage = new LocalSecureStorage(Username);
        storage.Initialize(MasterPassword);
        InitializedStorage = storage;

        bool isRegistered = await _chatService.RegisterAsync(Username, storage);

        if (isRegistered)
        {
            StatusMessage = "Подключение к защищенной сети...";
            _onAuthSuccess();
        }
        else
        {
            StatusMessage = "Ошибка регистрации. Возможно, имя уже занято сервером.";
            storage.Close();
            InitializedStorage = null;
            IsButtonsEnabled = true;
        }
    }

    // Логика кнопки "ВОЙТИ В СУЩЕСТВУЮЩИЙ ЧАТ"
    public void LoginLocal()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(MasterPassword))
        {
            StatusMessage = "Введите имя пользователя и мастер-пароль!";
            return;
        }

        StatusMessage = "Расшифровка локального хранилища...";

        // ИСПРАВЛЕНО: Инициализируем базу строго под введённым ником!
        var storage = new LocalSecureStorage(Username);
        bool isPasswordCorrect = storage.Initialize(MasterPassword);

        if (isPasswordCorrect)
        {
            InitializedStorage = storage; // Запоминаем открытую базу
            StatusMessage = "Вход успешно выполнен.";
            _onAuthSuccess();
        }
        else
        {
            StatusMessage = "Неверный мастер-пароль или профиль не существует!";
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
