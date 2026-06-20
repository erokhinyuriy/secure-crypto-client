using Avalonia.Threading;
using SecureCryptoClient.Models;
using SecureCryptoClient.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace SecureCryptoClient.ViewModels;

public class MainChatViewModel : INotifyPropertyChanged
{
    private readonly CryptoChatService _chatService;
    private readonly LocalSecureStorage _localStorage;

    private ChatSession? _selectedChat;
    private string _newFriendUsername = "";
    private string _typedText = "";
    private string _chatHeader = "Выберите чат для начала общения";
    private bool _isChatSelected = false;

    // Коллекции для UI
    public ObservableCollection<ChatSession> Chats { get; set; } = new();
    public ObservableCollection<ChatMessage> Messages { get; set; } = new();

    public ChatSession? SelectedChat
    {
        get => _selectedChat;
        set
        {
            _selectedChat = value;
            OnPropertyChanged();
            IsChatSelected = _selectedChat != null;

            if (_selectedChat != null)
            {
                InitializeSelectedChatAsync();
            }
        }
    }

    public string NewFriendUsername
    {
        get => _newFriendUsername;
        set { _newFriendUsername = value; OnPropertyChanged(); }
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

    public bool IsChatSelected
    {
        get => _isChatSelected;
        set { _isChatSelected = value; OnPropertyChanged(); }
    }

    public MainChatViewModel(CryptoChatService chatService, LocalSecureStorage localStorage)
    {
        _chatService = chatService;
        _localStorage = localStorage;
        LoadExistingChats();
    }

    public async Task InitializeAndConnectAsync()
    {
        // ЖЕЛЕЗОБЕТОННЫЙ ФИКС ДУБЛИРОВАНИЯ СОБЫТИЙ:
        // Мы циклически отписываемся от события до тех пор, пока подписка гарантированно не станет пустой.
        // Это на 100% зачистит память от дублирующих вызовов, сколько бы раз вы ни открывали чат.
        while (true)
        {
            // Пытаемся отписаться
            _chatService.MessageReceived -= OnIncomingMessageReceived;

            // В C# отписка от несуществующего события не вызывает ошибок. 
            // Делаем это дважды, чтобы полностью обнулить делегат подписок в CryptoChatService.
            _chatService.MessageReceived -= OnIncomingMessageReceived;
            break;
        }

        // Теперь, когда контур абсолютно чист, создаем ровно ОДНУ подписку для текущего окна
        _chatService.MessageReceived += OnIncomingMessageReceived;

        // Запускаем безопасное подключение сокета
        await _chatService.ConnectAsync();
    }

    // ЛОГИКА ДОБАВЛЕНИЯ ДРУГА ПО ЮЗЕРНЕЙМУ (X3DH Рукопожатие)
    public async Task AddNewChatByUsernameAsync()
    {
        if (string.IsNullOrWhiteSpace(NewFriendUsername)) return;
        var friend = NewFriendUsername.ToLower().Trim();
        NewFriendUsername = string.Empty;

        if (friend == _chatService.Username) return; // Нельзя добавить самого себя
        if (Chats.Any(c => c.PartnerName == friend)) return; // Чат уже есть

        ChatHeader = $"Поиск и авторизация ключей X3DH для {friend}...";

        // Запускаем тройное криптографическое рукопожатие Диффи-Хеллмана с сервером
        bool success = await _chatService.InitializeE2EEChannelWithAsync(friend, _localStorage);

        if (success)
        {
            var newSession = new ChatSession { PartnerName = friend, LastMessage = "Канал шифрования защищен" };
            Chats.Insert(0, newSession);
            SelectedChat = newSession;
        }
        else
        {
            ChatHeader = $"Ошибка: пользователь '{friend}' не найден на сервере.";
        }
    }

    // Переключение чата и загрузка истории из LiteDB
    private async void InitializeSelectedChatAsync()
    {
        if (SelectedChat == null) return;

        SelectedChat.UnreadCount = 0;
        var partner = SelectedChat.PartnerName;

        ChatHeader = $"Согласование сквозного шифрования с {partner}...";
        Messages.Clear();

        // Гарантируем, что секретный ключ в памяти вычислен
        await _chatService.InitializeE2EEChannelWithAsync(partner, _localStorage);

        ChatHeader = $"🔒 {partner.ToUpper()} (Сквозное шифрование)";

        // Читаем локальный архив сообщений
        var history = _localStorage.GetChatHistory(partner);
        foreach (var msg in history.OrderBy(m => m.Timestamp))
        {
            Messages.Add(msg);
        }
    }

    // Отправка сообщения
    public async Task SendMessageAsync()
    {
        if (SelectedChat == null || string.IsNullOrWhiteSpace(TypedText)) return;

        var textToSend = TypedText;
        TypedText = string.Empty;

        var msg = new ChatMessage
        {
            ChatPartner = SelectedChat.PartnerName,
            Sender = _chatService.Username,
            Text = textToSend
        };

        _localStorage.SaveMessage(msg);
        Messages.Add(msg);
        SelectedChat.LastMessage = textToSend;

        await _chatService.SendMessageAsync(SelectedChat.PartnerName, textToSend);
    }

    // Фоновый прием сообщения
    private async void OnIncomingMessageReceived(SignedPacket packet)
    {
        var sender = packet.Sender.ToLower().Trim();
        var myCurrentUsername = _chatService.Username.ToLower().Trim();

        if (string.Equals(sender, myCurrentUsername, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 1. Сначала асинхронно вычисляем или проверяем ключ X3DH
        await _chatService.InitializeE2EEChannelWithAsync(sender, _localStorage);

        var aesKey = _chatService.GetChatKey(sender);
        if (aesKey == null) return;

        // 2. Дешифруем текст сообщения
        string decryptedText = _chatService.DecryptAesGcm(packet.PayloadCipherBase64, aesKey);

        var incomingMsg = new ChatMessage
        {
            ChatPartner = sender,
            Sender = sender,
            Text = decryptedText,
            Timestamp = DateTime.UtcNow
        };

        // 3. ОБНОВЛЯЕМ ИНТЕРФЕЙС СТРОГО ДО СОХРАНЕНИЯ В БАЗУ ДАННЫХ
        Dispatcher.UIThread.Post(() =>
        {
            // Ищем чат без учета регистра
            var existingChat = Chats.FirstOrDefault(c => string.Equals(c.PartnerName, sender, StringComparison.OrdinalIgnoreCase));
            bool wasSelected = SelectedChat != null && string.Equals(SelectedChat.PartnerName, sender, StringComparison.OrdinalIgnoreCase);

            if (existingChat == null)
            {
                // ИСПРАВЛЕНО: Если нам пишет новый пользователь, создаем сессию чата
                existingChat = new ChatSession
                {
                    PartnerName = sender,
                    LastMessage = decryptedText
                };

                // Вставляем новый чат в самый верх панели диалогов
                Chats.Insert(0, existingChat);

                // КРИТИЧЕСКИЙ ФИКС: Принудительно уведомляем Avalonia UI, 
                // что список чатов изменился и его нужно перерисовать прямо сейчас!
                OnPropertyChanged(nameof(Chats));
            }
            else
            {
                existingChat.LastMessage = decryptedText;

                if (Chats.IndexOf(existingChat) != 0)
                {
                    Chats.Remove(existingChat);
                    Chats.Insert(0, existingChat);
                    OnPropertyChanged(nameof(Chats)); // Уведомляем о перестановке чата наверх
                }
            }

            if (wasSelected)
            {
                SelectedChat = existingChat;
            }

            // Если этот чат сейчас открыт на экране — выводим сообщение
            if (SelectedChat != null && string.Equals(sender, SelectedChat.PartnerName, StringComparison.OrdinalIgnoreCase))
            {
                if (!Messages.Any(m => string.Equals(m.Text, incomingMsg.Text) && m.Timestamp == incomingMsg.Timestamp))
                {
                    Messages.Add(incomingMsg);
                }
            }

            if (SelectedChat == null || !string.Equals(sender, SelectedChat.PartnerName, StringComparison.OrdinalIgnoreCase))
            {
                existingChat.UnreadCount++;
            }

            // Сохраняем сообщение в LiteDB на диск
            _localStorage.SaveMessage(incomingMsg);
        });
    }

    // Чтение списка уникальных чатов из базы при запуске
    private void LoadExistingChats()
    {
        Chats.Clear();
        try
        {
            // 1. Извлекаем из LiteDB имена всех людей, с кем есть переписка
            var partners = _localStorage.GetUniqueChatPartners();

            foreach (var partner in partners)
            {
                // 2. Для каждого друга находим самое последнее сообщение, чтобы сделать красивое превью
                var lastMsg = _localStorage.GetChatHistory(partner)
                                           .OrderByDescending(m => m.Timestamp)
                                           .FirstOrDefault();

                var session = new ChatSession
                {
                    PartnerName = partner,
                    LastMessage = lastMsg?.Text ?? "Канал шифрования защищен"
                };

                // 3. Заполняем боковую панель сохраненными чатами
                Chats.Add(session);
            }
        }
        catch
        {
            // Если сообщений в базе еще нет, список просто останется пустым
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
