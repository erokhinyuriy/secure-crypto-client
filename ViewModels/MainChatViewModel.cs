using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using SecureCryptoClient.Models;
using SecureCryptoClient.Services;

namespace SecureCryptoClient.ViewModels;

public class MainChatViewModel : INotifyPropertyChanged
{
    private readonly CryptoChatService _chatService;
    private readonly LocalSecureStorage _localStorage;

    private string _recipient = "";
    private string _typedText = "";
    private string _chatHeader = "Выберите собеседника";

    // Реактивная коллекция сообщений для отображения в списке Avalonia
    public ObservableCollection<ChatMessage> Messages { get; set; } = new();

    public string Recipient
    {
        get => _recipient;
        set
        {
            _recipient = value;
            OnPropertyChanged();
            LoadChatHistory(); // При смене собеседника — на лету подгружаем историю из LiteDB
        }
    }

    public string TypedText
    {
        get => _typedText;
        set { _typedText = value; OnPropertyChanged(); }
    }

    public string ChatHeader
    {
        get => _chatHeader;
        set { _chatHeader = value; OnPropertyChanged(); }
    }

    public MainChatViewModel(CryptoChatService chatService, LocalSecureStorage localStorage)
    {
        _chatService = chatService;
        _localStorage = localStorage;
        ChatHeader = $"Вы вошли как: {chatService.Username.ToUpper()}";
    }

    // Инициализация WebSocket-соединения с сервером
    public async Task InitializeAndConnectAsync()
    {
        // Передаем колбэк: что делать, когда из сети пришло новое E2EE-сообщение
        await _chatService.ConnectAsync(OnIncomingMessageReceived);
    }

    // Отправка сообщения
    public async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(Recipient) || string.IsNullOrWhiteSpace(TypedText)) return;

        var textToSend = TypedText;
        TypedText = string.Empty; // Сразу очищаем поле ввода для отзывчивости UI

        var msg = new ChatMessage
        {
            ChatPartner = Recipient.ToLower().Trim(),
            Sender = _chatService.Username, // Автор — мы
            Text = textToSend
        };

        // 1. Сохраняем в свою зашифрованную локальную БД
        _localStorage.SaveMessage(msg);

        // 2. Добавляем на экран
        Messages.Add(msg);

        // 3. Шифруем и пускаем в сеть через сервер
        await _chatService.SendMessageAsync(Recipient, textToSend);
    }

    // Срабатывает в фоновом потоке сети при получении пакета
    private void OnIncomingMessageReceived(ChatMessage incomingMsg)
    {
        // Сначала безвозвратно сохраняем зашифрованное на диске сообщение в LiteDB
        _localStorage.SaveMessage(incomingMsg);

        // Перебрасываем добавление в UI-поток Avalonia, чтобы не было фризов и крашей интерфейса
        Dispatcher.UIThread.Post(() =>
        {
            // Если у нас сейчас открыт чат именно с этим человеком — выводим на экран
            if (incomingMsg.ChatPartner == Recipient.ToLower().Trim())
            {
                Messages.Add(incomingMsg);
            }
        });
    }

    // Подгрузка истории из зашифрованной базы данных на диске
    private void LoadChatHistory()
    {
        Messages.Clear();
        if (string.IsNullOrWhiteSpace(Recipient)) return;

        var history = _localStorage.GetChatHistory(Recipient);
        foreach (var msg in history)
        {
            Messages.Add(msg);
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
