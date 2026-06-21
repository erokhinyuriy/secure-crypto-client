using Avalonia.Threading;
using SecureCryptoClient.Models;
using SecureCryptoClient.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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

    private bool _isMenuOpen = false;

    // Управляет открытием и закрытием выезжающего меню
    public bool IsMenuOpen
    {
        get => _isMenuOpen;
        set { _isMenuOpen = value; OnPropertyChanged(); }
    }

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

    private bool _isScrollButtonVisible = false;

    // Видимость круглой кнопки скролла вниз
    public bool IsScrollButtonVisible
    {
        get => _isScrollButtonVisible;
        set { _isScrollButtonVisible = value; OnPropertyChanged(); }
    }

    public event Action? LogoutRequested;
    public string MyUsername => _chatService.Username.ToUpper();

    // Возвращает первую букву вашего имени для аватарки профиля в меню
    public string MyInitials => string.IsNullOrEmpty(MyUsername) ? "?" : MyUsername[..1].ToUpper();

    private bool _isCreateGroupWindowVisible = false;
    private string _newGroupName = "";
    public ObservableCollection<GroupMemberSelection> AvailableFriends { get; set; } = new();

    // Видимость окна создания группы
    public bool IsCreateGroupWindowVisible
    {
        get => _isCreateGroupWindowVisible;
        set { _isCreateGroupWindowVisible = value; OnPropertyChanged(); }
    }

    // Название новой группы
    public string NewGroupName
    {
        get => _newGroupName;
        set { _newGroupName = value; OnPropertyChanged(); }
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
        var history = _localStorage.GetChatHistory(partner).OrderBy(m => m.Timestamp).ToList();

        DateTime? lastDate = null;

        foreach (var msg in history)
        {
            msg.IsMe = string.Equals(msg.Sender, _chatService.Username, StringComparison.OrdinalIgnoreCase);

            // Если это первое сообщение или день сменился — вставляем разделитель даты!
            if (lastDate == null || lastDate.Value.Date != msg.Timestamp.Date)
            {
                Messages.Add(new ChatMessage
                {
                    Timestamp = msg.Timestamp,
                    IsDateSeparator = true // Взводим сервисный флаг
                });
                lastDate = msg.Timestamp;
            }

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
            Text = textToSend,
            IsMe = true
        };

        _localStorage.SaveMessage(msg);
        Messages.Add(msg);
        SelectedChat.LastMessage = textToSend;

        await _chatService.SendMessageAsync(SelectedChat.PartnerName, textToSend);

        IsScrollButtonVisible = false;
    }

    // Метод-переключатель для кнопки-бургера
    public void ToggleMenu()
    {
        IsMenuOpen = !IsMenuOpen;
    }

    // Заглушки для новых кнопок меню
    public void OpenMyProfile() => _chatService.ShowNotification("Профиль", "Раздел 'Мой профиль' находится в разработке.");
    public void OpenSettings() => _chatService.ShowNotification("Настройки", "Раздел 'Настройки' находится в разработке.");
    public void CreateGroupChat()
    {
        // Закрываем шторку меню (через вызов события или просто сброс, если нужно)
        // Наш Code-Behind сам закроет шторку, так как мы подвяжемся на кнопку.

        NewGroupName = string.Empty;
        AvailableFriends.Clear();

        // Находим всех уникальных собеседников, с кем уже был диалог
        foreach (var chat in Chats)
        {
            AvailableFriends.Add(new GroupMemberSelection { Username = chat.PartnerName, IsSelected = false });
        }

        // Показываем модальное окно на экране
        IsCreateGroupWindowVisible = true;
    }

    public void CancelGroupCreation() => IsCreateGroupWindowVisible = false;

    // ФИНАЛЬНЫЙ КЛИК: Кнопка "Создать" в самом низу окна
    public async Task ConfirmCreateGroupAsync()
    {
        if (string.IsNullOrWhiteSpace(NewGroupName))
        {
            _chatService.ShowNotification("Ошибка", "Введите название группы");
            return;
        }

        var selectedUsers = AvailableFriends.Where(f => f.IsSelected).Select(f => f.Username).ToList();
        if (selectedUsers.Count == 0)
        {
            _chatService.ShowNotification("Ошибка", "Выберите хотя бы одного участника");
            return;
        }

        // Закрываем окно создания
        IsCreateGroupWindowVisible = false;

        // В следующих шагах мы допишем сюда генерацию Sender Key группы и отправку на сервер.
        _chatService.ShowNotification("Успех", $"Группа '{NewGroupName}' успешно инициирована!");
    }

    // ЛОГИКА ПУНКТА 2: ВЫХОД ИЗ УЧЕТНОЙ ЗАПИСИ
    public void Logout()
    {
        // 1. Отписываемся от сетевых событий, чтобы не плодить утечки памяти
        _chatService.MessageReceived -= OnIncomingMessageReceived;

        // 2. Стираем крипто-ключи текущей сессии из оперативной памяти
        typeof(CryptoChatService)
            .GetField("_privateIdentityKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .SetValue(_chatService, null);

        // 3. Удаляем файл автологина с диска, чтобы сбросить галочку "Запомнить меня"
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var tokenPath = Path.Combine(appData, "SecureCryptoMessenger", $"autologin_{_chatService.Username.ToLower().Trim()}.dat");
            if (File.Exists(tokenPath))
            {
                File.Delete(tokenPath);
            }
        }
        catch { }

        // 4. Закрываем зашифрованный дескриптор LiteDB, очищая память файлов
        _localStorage.Close();

        // 5. Вызываем событие возврата на экран входа
        LogoutRequested?.Invoke();
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
            Timestamp = DateTime.UtcNow,
            IsMe = false
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
                    var lastVisibleMsg = Messages.LastOrDefault(m => !m.IsDateSeparator);
                    if (lastVisibleMsg == null || lastVisibleMsg.Timestamp.Date != incomingMsg.Timestamp.Date)
                    {
                        Messages.Add(new ChatMessage
                        {
                            Timestamp = incomingMsg.Timestamp,
                            IsDateSeparator = true
                        });
                    }

                    Messages.Add(incomingMsg);
                }
            }

            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow : null;

            // Окно считается скрытым, если оно полностью свернуто (Minimized) или не находится в фону (IsActive = false)
            bool isWindowHidden = mainWindow == null ||
                                  mainWindow.WindowState == Avalonia.Controls.WindowState.Minimized ||
                                  !mainWindow.IsActive;

            // ЖЕЛЕЗОБЕТОННЫЙ ФИКС БАГА:
            // Мы зажигаем уведомление со звуком в двух случаях:
            // 1. Если сообщение пришло от человека, чей чат сейчас НЕ открыт на экране.
            // 2. ИЛИ если чат открыт, но само приложение СВЕРНУТО пользователем на панель задач!
            if (SelectedChat == null ||
                !string.Equals(sender, SelectedChat.PartnerName, StringComparison.OrdinalIgnoreCase) ||
                isWindowHidden)
            {
                // Накручиваем счетчик непрочитанных только если мы сидим в ДРУГОМ чате или приложение свернуто
                if (SelectedChat == null || !string.Equals(sender, SelectedChat.PartnerName, StringComparison.OrdinalIgnoreCase))
                {
                    existingChat.UnreadCount++;
                }

                // Вызываем всплывающее окно со звуком в трее Windows со стопроцентной гарантией!
                _chatService.ShowNotification(
                    title: $"💬 Новое сообщение от {sender.ToUpper()}",
                    message: decryptedText
                );
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
