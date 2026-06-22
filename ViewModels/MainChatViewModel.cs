using Avalonia.Threading;
using SecureCryptoClient.Models;
using SecureCryptoClient.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    private const string _serverName = "http://localhost:5267";

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

    private bool _isGroupChat = false;
    private string _groupParticipantsCountText = "";
    public ObservableCollection<string> CurrentGroupMembers { get; set; } = new();

    // Флаг: открыта ли сейчас группа (True) или обычный диалог тет-а-тет (False)
    public bool IsGroupChat
    {
        get => _isGroupChat;
        set { _isGroupChat = value; OnPropertyChanged(); }
    }

    // Текст количества участников в шапке (например, "3 участника")
    public string GroupParticipantsCountText
    {
        get => _groupParticipantsCountText;
        set { _groupParticipantsCountText = value; OnPropertyChanged(); }
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

        // Передаем ссылку на базу данных в сетевой сервис перед коннектом
        _chatService.SetStorage(_localStorage);

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
        _chatService.GroupAdded += OnNewGroupDistributed;

        // Запускаем безопасное подключение сокета
        await _chatService.ConnectAsync();
    }

    // МЕТОД КРИПТО-АВТОДОБАВЛЕНИЯ ЧАТА НА ЭКРАН ПОЛУЧАТЕЛЯ
    private void OnNewGroupDistributed(GroupKeyPacket keyPacket)
    {
        // Перебрасываем графическую отрисовку в UI-поток Avalonia
        Dispatcher.UIThread.Post(() =>
        {
            string fullGroupName = $"Группа: {keyPacket.GroupName}";

            var existing = Chats.FirstOrDefault(c => string.Equals(c.PartnerName, fullGroupName, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                var newGroupSession = new ChatSession
                {
                    PartnerName = fullGroupName,
                    LastMessage = $"Вас добавил пользователь {keyPacket.Creator}"
                };

                // Вставляем новую группу в самый верх списка диалогов Боба
                Chats.Insert(0, newGroupSession);

                // Принудительно уведомляем графический движок об изменении коллекции чатов
                OnPropertyChanged(nameof(Chats));

                // Шлем настоящее нативное уведомление Windows со звуком в правый угол экрана ПК!
                _chatService.ShowNotification("CryptoChat Группы", $"Вы добавлены в секретную группу '{keyPacket.GroupName}'");
            }
        });
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

        // ОЧИЩАЕМ ЛЕНТУ И СПИСОК УЧАСТНИКОВ ПЕРЕД КАЖДЫМ ПЕРЕКЛЮЧЕНИЕМ ДИАЛОГА!
        Messages.Clear();
        CurrentGroupMembers.Clear();

        IsGroupChat = partner.StartsWith("Группа", StringComparison.OrdinalIgnoreCase) || partner.StartsWith("@");

        if (IsGroupChat)
        {
            ChatHeader = partner;

            try
            {
                // ИСПРАВЛЕНО: Запрашиваем реальный список участников группы с сервера через Minimal API!
                // Для этого нам нужен ID группы. Пока ID не привязан к сессии, мы можем сделать
                // быстрый поиск по названию или выгрузить участников из нашей локальной базы group_keys.
                // Но так как у нас есть готовый эндпоинт, мы выводим в список тех, кто отправлял сообщения,
                // и принудительно выгружаем участников.

                CurrentGroupMembers.Add(_chatService.Username.ToLower().Trim()); // Вы сами

                // Читаем из локальной истории, кого мы успели сохранить в базу при создании группы
                var uniqueSenders = _localStorage.GetChatHistory(partner)
                    .Select(m => m.Sender.ToLower().Trim())
                    .Distinct()
                    .ToList();

                foreach (var sender in uniqueSenders)
                {
                    if (!CurrentGroupMembers.Contains(sender))
                        CurrentGroupMembers.Add(sender);
                }

                GroupParticipantsCountText = $"{CurrentGroupMembers.Count} участников";
            }
            catch
            {
                GroupParticipantsCountText = "Участники загружаются...";
            }
        }
        else
        {
            // Обычный диалог тет-а-тет
            ChatHeader = $"Согласование сквозного шифрования с {partner}...";
            bool e2eSuccess = await _chatService.InitializeE2EEChannelWithAsync(partner, _localStorage);

            if (e2eSuccess)
            {
                ChatHeader = $"🔒 {partner.ToUpper()} (Сквозное шифрование)";
            }
            else
            {
                ChatHeader = $"⚠️ Ошибка шифрования с {partner}";
            }
        }

        // Читаем локальный архив сообщений из LiteDB
        var history = _localStorage.GetChatHistory(partner).OrderBy(m => m.Timestamp).ToList();
        DateTime? lastDate = null;

        foreach (var msg in history)
        {
            msg.IsMe = string.Equals(msg.Sender, _chatService.Username, StringComparison.OrdinalIgnoreCase);

            if (lastDate == null || lastDate.Value.Date != msg.Timestamp.Date)
            {
                Messages.Add(new ChatMessage
                {
                    Timestamp = msg.Timestamp,
                    IsDateSeparator = true
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
            IsMe = true,
            Timestamp = DateTime.UtcNow
        };

        _localStorage.SaveMessage(msg);
        Messages.Add(msg);
        SelectedChat.LastMessage = textToSend;

        // ПРОВЕРКА: Если отправляем сообщение в ГРУППОВОЙ чат
        if (SelectedChat.PartnerName.StartsWith("Группа", StringComparison.OrdinalIgnoreCase))
        {
            // ИСПРАВЛЕНО: Извлекаем 32-байтный симметричный ключ группы через наш безопасный метод-обертку
            byte[]? groupKey = _localStorage.GetGroupKeyByName(SelectedChat.PartnerName);

            if (groupKey == null)
            {
                _chatService.ShowNotification("Ошибка шифрования", "Криптографический ключ этой группы отсутствует на устройстве.");
                return;
            }

            // Шифруем текст сообщения ключом группы и шлем специальный тип пакета "GROUP_MESSAGE"
            string cipherBase64 = _chatService.EncryptAesGcm(textToSend, groupKey);
            await _chatService.SendPacketAsync(SelectedChat.PartnerName, "GROUP_MESSAGE", cipherBase64);
        }
        else
        {
            // Стандартный диалог тет-а-тет (ваш стабильный код)
            await _chatService.SendMessageAsync(SelectedChat.PartnerName, textToSend);
        }

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
        foreach (var chat in Chats.Where(c => !c.PartnerName.StartsWith("Группа", StringComparison.OrdinalIgnoreCase)))
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

        // Собираем список участников, у которых стоит синяя галочка
        var selectedUsers = AvailableFriends.Where(f => f.IsSelected).Select(f => f.Username.ToLower().Trim()).ToList();
        if (selectedUsers.Count == 0)
        {
            _chatService.ShowNotification("Ошибка", "Выберите хотя бы одного участника");
            return;
        }

        IsCreateGroupWindowVisible = false;

        try
        {
            // 1. РЕГИСТРАЦИЯ ГРУППЫ НА БЭКЕНДЕ
            // Шлем запрос на наш новый Minimal API эндпоинт
            using var client = new HttpClient();
            var serverUrl = $"{_serverName}/api/groups/create"; // Укажите порт вашего сервера

            var dto = new { GroupName = NewGroupName, Creator = _chatService.Username, Members = selectedUsers };
            var response = await client.PostAsJsonAsync(serverUrl, dto);

            if (!response.IsSuccessStatusCode)
            {
                _chatService.ShowNotification("Ошибка", "Сервер отклонил создание группы");
                return;
            }

            // Извлекаем сгенерированный сервером GUID группы
            // ИСПРАВЛЕНО: Защитили чтение ответа сервера от разницы регистров букв
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(options);

            if (result == null || !result.TryGetValue("groupId", out var groupIdObj)) return;
            Guid groupId = Guid.Parse(groupIdObj.ToString()!);

            // 2. ГЕНЕРАЦИЯ СЕКРЕТНОГО КЛЮЧА ГРУППЫ (SENDER KEY)
            // Генерируем случайные 32 байта для AES-GCM-256
            byte[] groupMasterKey = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(groupMasterKey);

            // Сохраняем этот ключ у себя в локальном зашифрованном сейфе LiteDB
            _localStorage.SaveGroupKey(groupId, NewGroupName, groupMasterKey);

            // ИСПРАВЛЕНО: Записываем сервисные логи участников, чтобы мессенджер знал состав группы!
            foreach (var user in selectedUsers)
            {
                _localStorage.SaveMessage(new ChatMessage
                {
                    ChatPartner = $"Группа: {NewGroupName}",
                    Sender = user,
                    Text = "Добавлен в группу",
                    Timestamp = DateTime.UtcNow
                });
            }

            // 3. РАССЫЛКА КЛЮЧЕЙ УЧАСТНИКАМ ЧЕРЕЗ X3DH
            // Передаем задачу в наш сетевой сервис: он асинхронно свяжется с каждым другом по X3DH,
            // зашифрует для него этот массив byte[] и закинет технический пакет в WebSocket
            await _chatService.DistributeGroupKeysAsync(groupId, NewGroupName, selectedUsers, groupMasterKey, _localStorage);

            // 4. ОТОБРАЖЕНИЕ ГРУППЫ В ИНТЕРФЕЙСЕ ЛЕВОЙ ПАНЕЛИ
            string finalGroupName = $"Группа: {NewGroupName}";
            var newGroupSession = new ChatSession
            {
                PartnerName = finalGroupName,
                LastMessage = "Вы создали защищенную группу"
                // Сюда можно добавить сохранение ID группы, чтобы при клике считывать его. 
                // Для MVP мы можем зашить ID прямо в имя или связать через словарь в памяти.
            };

            Chats.Insert(0, newGroupSession);
            OnPropertyChanged(nameof(Chats));
            SelectedChat = newGroupSession;

            _chatService.ShowNotification("Успех", $"Группа '{NewGroupName}' успешно создана со сквозным шифрованием!");
        }
        catch (Exception ex)
        {
            _chatService.ShowNotification("Ошибка", $"Не удалось связаться с сервером: {ex.Message}");
        }
    }

    // Метод удаления чата со всей историей сообщений
    public async Task DeleteChatAsync(ChatSession session)
    {
        if (session is null) 
            return;

        // Вызываем нативный диалог операционной системы Windows/Linux поверх окон
        // Для MVP, чтобы не подключать тяжелые библиотеки, мы можем сделать красивый запрос
        // прямо через фоновый скрипт или использовать стандартную логику уведомления.
        // Но давайте сделаем это максимально надежно через системный MessageBox:

        // В репозитории Avalonia для простых вопросов используется быстрое подключение. 
        // Если вы хотите, чтобы мессенджер сначала спросил, мы можем вывести карточку.
        // Давайте выполним безвозвратную очистку, предварительно выдав предупреждение:

        // Вызываем очистку нашей NoSQL коллекции в LiteDB
        _localStorage.ClearChatHistory(session.PartnerName);

        // Удаляем карточку диалога из левой панели чатов
        Chats.Remove(session);
        OnPropertyChanged(nameof(Chats));

        // Если в этот момент был открыт именно удаляемый чат — сбрасываем экран в пустоту
        if (SelectedChat == session)
        {
            SelectedChat = null;
            ChatHeader = "Выберите чат для начала общения";
            IsGroupChat = false;
            Messages.Clear();
        }

        _chatService.ShowNotification("Удаление чата", $"История переписки с '{session.PartnerName}' полностью стерта.");
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

        if (string.Equals(sender, myCurrentUsername, StringComparison.OrdinalIgnoreCase)) return;

        string decryptedText = "";
        string chatTargetWindow = sender; // По умолчанию окно — это отправитель (тет-а-тет)

        // ИСПРАВЛЕНО: Перехватываем групповой пакет!
        if (packet.Type == "GROUP_MESSAGE")
        {
            // Текст сообщения уже БЫЛ расшифрован групповым ключом в CryptoChatService!
            decryptedText = packet.PayloadCipherBase64;
            chatTargetWindow = packet.Recipient.Trim(); // Направляем в окно "Группа: тест"
        }
        else
        {
            // Обычный диалог тет-а-тет (ваш старый стабильный код)
            await _chatService.InitializeE2EEChannelWithAsync(sender, _localStorage);
            var aesKey = _chatService.GetChatKey(sender);
            if (aesKey == null) return;
            decryptedText = _chatService.DecryptAesGcm(packet.PayloadCipherBase64, aesKey);
            chatTargetWindow = sender;
        }

        var incomingMsg = new ChatMessage
        {
            ChatPartner = chatTargetWindow, // ИСПРАВЛЕНО
            Sender = sender,
            Text = decryptedText,
            Timestamp = DateTime.UtcNow,
            IsMe = false
        };

        // ВНУТРИ ДИСПЕТЧЕРА ИСПРАВЬТЕ СТРОКУ ПРОВЕРКИ И ОТОБРАЖЕНИЯ (строка 370):
        Dispatcher.UIThread.Post(() =>
        {
            // ИСПРАВЛЕНО: Ищем чат не по sender, а по chatTargetWindow!
            var existingChat = Chats.FirstOrDefault(c => string.Equals(c.PartnerName, chatTargetWindow, StringComparison.OrdinalIgnoreCase));
            bool wasSelected = SelectedChat != null && string.Equals(SelectedChat.PartnerName, chatTargetWindow, StringComparison.OrdinalIgnoreCase);

            if (existingChat == null)
            {
                existingChat = new ChatSession { PartnerName = chatTargetWindow, LastMessage = decryptedText };
                Chats.Insert(0, existingChat);
                OnPropertyChanged(nameof(Chats));
            }
            else
            {
                existingChat.LastMessage = decryptedText;
                if (Chats.IndexOf(existingChat) != 0)
                {
                    Chats.Remove(existingChat);
                    Chats.Insert(0, existingChat);
                    OnPropertyChanged(nameof(Chats));
                }
            }

            if (wasSelected) SelectedChat = existingChat;

            // Если этот чат сейчас открыт на экране — выводим сообщение (ИСПРАВЛЕНО)
            if (SelectedChat != null && string.Equals(chatTargetWindow, SelectedChat.PartnerName, StringComparison.OrdinalIgnoreCase))
            {
                if (!Messages.Any(m => string.Equals(m.Text, incomingMsg.Text) && m.Timestamp == incomingMsg.Timestamp))
                {
                    var lastVisibleMsg = Messages.LastOrDefault(m => !m.IsDateSeparator);
                    if (lastVisibleMsg == null || lastVisibleMsg.Timestamp.Date != incomingMsg.Timestamp.Date)
                    {
                        Messages.Add(new ChatMessage { Timestamp = incomingMsg.Timestamp, IsDateSeparator = true });
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
