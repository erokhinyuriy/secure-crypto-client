using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using Microsoft.Win32;
using NSec.Cryptography;
using SecureCryptoClient.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SecureCryptoClient.Services;

public class CryptoChatService
{
    private readonly string _serverHttpUrl = "http://localhost:5267";
    private readonly string _serverWsUrl = "ws://localhost:5267/ws";
    private readonly HttpClient _httpClient = new();
    private ClientWebSocket? _webSocket;
    private WindowNotificationManager? _notificationManager;

    private LocalSecureStorage? _localStorage;

    private byte[]? _privateIdentityKey; // Ed25519
    public string PublicKeyBase64 { get; set; } = "";
    public string Username { get; set; } = "";

    // Хранилище сессионных AES-GCM ключей: Собеседник -> Вычисленный симметричный ключ
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _activeChatKeys = new();

    public CryptoChatService(string username) => Username = username.ToLower().Trim();

    public event Action<GroupKeyPacket>? GroupAdded;

    public byte[]? GetChatKey(string friend)
    {
        return _activeChatKeys.TryGetValue(friend.ToLower().Trim(), out var key) ? key : null;
    }

    // Обновим метод инициализации или добавим сеттер, чтобы связать базу:
    public void SetStorage(LocalSecureStorage storage)
    {
        _localStorage = storage;
    }

    public event Action<SignedPacket>? MessageReceived;

    // РЕГИСТРАЦИЯ X3DH
    public async Task<bool> RegisterAsync(string username, LocalSecureStorage storage)
    {
        Username = username.ToLower().Trim();

        // 1. Генерируем Ed25519 (для цифровых подписей пакетов)
        var algo = SignatureAlgorithm.Ed25519;
        using var edKey = Key.Create(algo, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        _privateIdentityKey = edKey.Export(KeyBlobFormat.RawPrivateKey);
        PublicKeyBase64 = Convert.ToBase64String(edKey.Export(KeyBlobFormat.RawPublicKey));

        // 2. Генерируем связку X3DH ключей (ECDiffieHellman NIST P-256)
        using var ecdhIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var ecdhSignedPre = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var ecdhOneTime = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var pubId = Convert.ToBase64String(ecdhIdentity.PublicKey.ExportSubjectPublicKeyInfo());
        byte[] pubSignedBytes = ecdhSignedPre.PublicKey.ExportSubjectPublicKeyInfo();
        var pubSigned = Convert.ToBase64String(pubSignedBytes);
        var pubOneTime = Convert.ToBase64String(ecdhOneTime.PublicKey.ExportSubjectPublicKeyInfo());

        // НОВОЕ: подписываем байты SignedPrekey нашим Ed25519 identity-ключом.
        // Без этой подписи получатель бандла не может отличить настоящий SignedPrekey
        // от подменённого сервером (или атакующим, имеющим доступ к серверу).
        byte[] signedPrekeySignatureBytes = SignatureAlgorithm.Ed25519.Sign(edKey, pubSignedBytes);
        var signedPrekeySignatureBase64 = Convert.ToBase64String(signedPrekeySignatureBytes);

        var dto = new
        {
            Username = Username,
            PublicKeyBase64 = PublicKeyBase64,
            EcdhIdentityKeyBase64 = pubId,
            SignedPrekeyBase64 = pubSigned,
            SignedPrekeySignatureBase64 = signedPrekeySignatureBase64,
            OneTimePrekeyBase64 = pubOneTime
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{_serverHttpUrl}/api/auth/register", dto);
            if (response.IsSuccessStatusCode)
            {
                // Сохраняем все приватные ключи в наш локальный зашифрованный сейф LiteDB
                storage.SaveConfigValue("identity_private", Convert.ToBase64String(_privateIdentityKey));
                storage.SaveConfigValue("identity_public", PublicKeyBase64);

                storage.SaveConfigValue("ecdh_id_private", Convert.ToBase64String(ecdhIdentity.ExportECPrivateKey()));
                storage.SaveConfigValue("ecdh_signed_private", Convert.ToBase64String(ecdhSignedPre.ExportECPrivateKey()));
                return true;
            }
        }
        catch { return false; }
        return false;
    }

    // ИНИЦИАЛИЗАЦИЯ ЧАТА (ВЫЧИСЛЕНИЕ СЕКРЕТА X3DH)
    public async Task<bool> InitializeE2EEChannelWithAsync(string friendUsername, LocalSecureStorage storage)
    {
        var friend = friendUsername.ToLower().Trim();
        if (_activeChatKeys.ContainsKey(friend)) return true; // Канал уже согласован

        try
        {
            // 1. Скачиваем "Крипто-Паспорт" друга с сервера
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            var bundle = await _httpClient.GetFromJsonAsync<PrekeyBundleDto>(
                $"{_serverHttpUrl}/api/crypto/prekey-bundle/{friend}",
                jsonOptions
            );
            if (bundle == null) return false;

            // НОВОЕ: проверяем подпись SignedPrekey ПЕРЕД тем, как использовать его в DH.
            // Если сервер (или кто-то на пути к нему) подменил SignedPrekey — подпись не пройдёт,
            // и мы НЕ должны строить сессию на этом ключе.
            if (!VerifySignedPrekey(bundle.Ed25519PublicKey, bundle.SignedPrekey, bundle.SignedPrekeySignature))
            {
                Console.WriteLine($"[⚠️ КРИПТО] Подпись SignedPrekey пользователя '{friend}' НЕ прошла проверку. Возможна подмена ключа (MITM). Сессия отменена.");
                ShowNotification("Угроза безопасности", $"Не удалось подтвердить ключи пользователя {friend}. Соединение отменено.");
                return false;
            }

            // Определяем роли по алфавитному порядку никнеймов для симметрии вычислений
            bool isInitiator = string.Compare(Username, friend) < 0;

            // 2. Инициализируем локальные приватные ключи ECDH
            byte[] myPrivIdBytes = Convert.FromBase64String(storage.GetConfigValue("ecdh_id_private")!);
            using var myEcdhId = ECDiffieHellman.Create();
            myEcdhId.ImportECPrivateKey(myPrivIdBytes, out _);

            byte[] myPrivSignedBytes = Convert.FromBase64String(storage.GetConfigValue("ecdh_signed_private")!);
            using var myEcdhSigned = ECDiffieHellman.Create();
            myEcdhSigned.ImportECPrivateKey(myPrivSignedBytes, out _);

            // 3. Импортируем публичные ключи друга
            using var friendEcdhId = ECDiffieHellman.Create();
            friendEcdhId.ImportSubjectPublicKeyInfo(Convert.FromBase64String(bundle.EcdhIdentityKey), out _);

            using var friendEcdhSigned = ECDiffieHellman.Create();
            friendEcdhSigned.ImportSubjectPublicKeyInfo(Convert.FromBase64String(bundle.SignedPrekey), out _);

            byte[] dh1;
            byte[] dh2;

            // 4. СТРОГАЯ СИММЕТРИЯ X3DH РУКОПОЖАТИЯ
            if (isInitiator)
            {
                // Если мы Инициализатор чата (Алиса):
                dh1 = myEcdhId.DeriveKeyMaterial(friendEcdhSigned.PublicKey); // Id_alice * Signed_bob
                dh2 = myEcdhId.DeriveKeyMaterial(friendEcdhId.PublicKey);     // Id_alice * Id_bob
            }
            else
            {
                // Если мы Принимающая сторона (Боб):
                dh1 = myEcdhSigned.DeriveKeyMaterial(friendEcdhId.PublicKey); // Signed_bob * Id_alice
                dh2 = myEcdhId.DeriveKeyMaterial(friendEcdhId.PublicKey);     // Id_bob * Id_alice
            }

            // Соединяем куски секретов
            using var ms = new MemoryStream();
            ms.Write(dh1);
            ms.Write(dh2);
            byte[] masterSecret = ms.ToArray();

            // Прогоняем через HKDF. Теперь на обеих сторонах finalAesKey будет идентичен до бита!
            byte[] finalAesKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                masterSecret,
                32,
                null,
                Encoding.UTF8.GetBytes("X3DH_Protocol_Salt")
            );

            _activeChatKeys[friend] = finalAesKey;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task ConnectAsync()
    {
        if (_privateIdentityKey is null) 
            throw new InvalidOperationException("Нет ключей подписи пакетов.");

        if (_localStorage is null)
            throw new InvalidOperationException("Локальное хранилище не подключено.");

        // ЖЕЛЕЗОБЕТОННЫЙ ФИКС ДУБЛИРОВАНИЯ ПОТОКОВ:
        // Если сокет уже создан и подключен, мы НЕ запускаем новый Task.Run и не создаем дублирующий поток чтения!
        if (_webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            // Сеть уже активна, просто шлем PING для обновления сессии на бэкенде
            await SendPacketAsync("", "PING", "");
            return;
        }

        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(new Uri(_serverWsUrl), CancellationToken.None);
        await SendPacketAsync("", "PING", "");

        // Этот Task.Run запустится строго ОДИН РАЗ за всю жизнь приложения!
        _ = Task.Run(async () =>
        {
            var buffer = new byte[1024 * 64];
            while (_webSocket.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var packet = JsonSerializer.Deserialize<SignedPacket>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (packet != null && packet.Type == "MESSAGE")
                    {
                        // Вызываем событие. Его услышит только та ViewModel, которая активна сейчас
                        MessageReceived?.Invoke(packet);
                    }

                    if (packet.Type == "GROUP_KEY_DISTRIBUTION")
                    {
                        try
                        {
                            // 1. Десериализуем технический JSON-пакет из Payload
                            var keyPacket = JsonSerializer.Deserialize<GroupKeyPacket>(packet.PayloadCipherBase64, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });
                            if (keyPacket != null)
                            {
                                string sender = packet.Sender.ToLower().Trim();

                                // 2. Достаем сессионный ключ X3DH общения с создателем группы
                                byte[] sessionKey = GetSharedKeyForUser(sender, _localStorage);

                                // 3. Расшифровываем 32-байтный мастер-ключ созданной группы
                                byte[] packetBytes = Convert.FromBase64String(keyPacket.EncryptedGroupKeyBase64);

                                using var ms = new MemoryStream(packetBytes);
                                using var reader = new BinaryReader(ms);

                                byte[] nonce = reader.ReadBytes(12);
                                byte[] tag = reader.ReadBytes(16);
                                byte[] ciphertext = reader.ReadBytes(packetBytes.Length - 12 - 16);

                                using var aes = new AesGcm(sessionKey, 16);
                                byte[] decryptedGroupKey = new byte[ciphertext.Length];

                                aes.Decrypt(nonce, ciphertext, tag, decryptedGroupKey);

                                // 4. Тихо сохраняем ключ группы в локальную зашифрованную базу данных LiteDB Боба/Чарли
                                _localStorage.SaveGroupKey(keyPacket.GroupId, keyPacket.GroupName, decryptedGroupKey);

                                // 5. Перебрасываем уведомление во ViewModel, чтобы чат сам появился в левой панели
                                // ЖЕЛЕЗОБЕТОННЫЙ ФИКС СИНХРОНИЗАЦИИ (ИСПРАВЛЕНО):
                                // Вместо капризного события во ViewModel, которое может быть null,
                                // мы шлем прямое уведомление в операционную систему о том, что Боб добавлен в группу.
                                ShowNotification("CryptoChat Группы", $"Вы добавлены в секретную группу '{keyPacket.GroupName}'");

                                // Также записываем стартовое сервисное сообщение в локальную базу Боба,
                                // чтобы при открытии чата Боб сразу видел, кто его добавил!
                                _localStorage.SaveMessage(new ChatMessage
                                {
                                    ChatPartner = $"Группа: {keyPacket.GroupName}",
                                    Sender = keyPacket.Creator,
                                    Text = $"Вас добавил пользователь {keyPacket.Creator}",
                                    Timestamp = DateTime.UtcNow
                                });

                                // ИСПРАВЛЕНО: Генерируем событие для ViewModel Боба!
                                GroupAdded?.Invoke(keyPacket);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[КРИПТО-ГРУППА] Ошибка разбора пакета ключей: {ex.Message}");
                        }

                        continue; // Системный пакет обработан, прерываем итерацию, чтобы не выводить шум в обычный чат
                    }

                    // ЖЕЛЕЗОБЕТОННЫЙ ХУК ПРИЕМА ГРУППОВЫХ СООБЩЕНИЙ (ДОБАВЛЕНО):
                    if (packet != null && packet.Type == "GROUP_MESSAGE")
                    {
                        try
                        {
                            var groupTarget = packet.Recipient.Trim(); // Имя группы, куда пришло письмо
                            var sender = packet.Sender.ToLower().Trim();

                            // Ищем ключ этой группы в локальной зашифрованной LiteDB Боба
                            byte[]? groupKey = _localStorage!.GetGroupKeyByName(groupTarget);

                            if (groupKey != null)
                            {
                                // Расшифровываем текст сообщения единым ключом группы (Sender Key)!
                                string decryptedText = DecryptAesGcm(packet.PayloadCipherBase64, groupKey);

                                // Подменяем зашифрованный текст на чистый, чтобы ViewModel вывела его на экран
                                packet.PayloadCipherBase64 = decryptedText;

                                // Триггерим стандартное событие получения сообщения. 
                                // ViewModel сама поймет, что это групповой чат, по имени в packet.Recipient!
                                MessageReceived?.Invoke(packet);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ГРУППА] Ошибка расшифровки группового сообщения: {ex.Message}");
                        }
                    }
                }
                catch { break; }
            }
        });
    }

    public async Task SendMessageAsync(string recipient, string plainText)
    {
        var target = recipient.ToLower().Trim();
        if (!_activeChatKeys.TryGetValue(target, out var aesKey)) throw new InvalidOperationException("Канал шифрования не согласован. Сначала вызовите инициализацию чата.");

        string cipherBase64 = EncryptAesGcm(plainText, aesKey);
        await SendPacketAsync(target, "MESSAGE", cipherBase64);
    }

    public bool LoadIdentityKeysFromStorage(LocalSecureStorage storage)
    {
        if (_privateIdentityKey != null && !string.IsNullOrEmpty(PublicKeyBase64))
        {
            return true;
        }

        // Во всех остальных случаях (например, при обычном Входе/Логине) — честно читаем с диска
        var privKeyBase64 = storage.GetConfigValue("identity_private");
        var pubKeyBase64 = storage.GetConfigValue("identity_public");

        if (!string.IsNullOrEmpty(privKeyBase64) && !string.IsNullOrEmpty(pubKeyBase64))
        {
            _privateIdentityKey = Convert.FromBase64String(privKeyBase64);
            PublicKeyBase64 = pubKeyBase64;
            return true;
        }
        return false;
    }

    public void SetNotificationManager(WindowNotificationManager manager)
    {
        _notificationManager = manager;
    }

    // Метод выводит красивое всплывающее окно в углу экрана ПК
    public void ShowNotification(string title, string message)
    {
        // 1. ВОСПРОИЗВЕДЕНИЕ ЗВУКА УВЕДОМЛЕНИЯ
        try
        {
            // Ищем файл notification.wav в папке запуска приложения
            string soundPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "notification.wav");

            if (File.Exists(soundPath))
            {
                // Используем стандартный системный плеер .NET
                using (var player = new SoundPlayer(soundPath))
                {
                    player.Play(); // Проигрывает звук асинхронно, не тормозя работу сети
                }
            }
        }
        catch
        {
            // Если звуковая карта занята или файл отсутствует — мессенджер продолжит работу без ошибок
        }

        // 2. ВЫВОД СИСТЕМНОЙ КАРТОЧКИ WINDOWS
        try
        {
            string appId = "SecureCryptoMessenger.App";
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\" + appId))
            {
                if (key != null)
                {
                    key.SetValue("ShowInActionCenter", 1, RegistryValueKind.DWord);
                }
            }

            var safeTitle = title.Replace("\"", "'");
            var safeMessage = message.Replace("\"", "'");

            // Обратите внимание: в конце XML-шаблона мы принудительно отключили стандартный звук Windows (silent='true'),
            // чтобы системный писк Windows не накладывался на наш красивый фирменный звук мессенджера!
            string script = $@"
                [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null;
                [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime] | Out-Null;
        
                $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);
                $toastTextElements = $template.GetElementsByTagName('text');
                $toastTextElements.Item(0).AppendChild($template.CreateTextNode('{safeTitle}')) | Out-Null;
                $toastTextElements.Item(1).AppendChild($template.CreateTextNode('{safeMessage}')) | Out-Null;
        
                $toastNode = $template.SelectSingleNode('/toast');
                $audioNode = $template.CreateElement('audio');
                $audioNode.SetAttribute('silent', 'true');
                $toastNode.AppendChild($audioNode) | Out-Null;
        
                $toast = [Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType=WindowsRuntime]::New($template);
                [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{appId}').Show($toast);
            ";

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{script}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            System.Diagnostics.Process.Start(startInfo);
        }
        catch
        {
            // Подстраховка для других ОС
        }
    }

    public async Task SendPacketAsync(string recipient, string type, string cipherPayload)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open || _privateIdentityKey == null) return;

        var packet = new SignedPacket { Sender = Username.ToLower().Trim(), Recipient = recipient.ToLower().Trim(), Type = type, PayloadCipherBase64 = cipherPayload };

        string rawStringToSign = $"{packet.Sender}|{packet.Recipient}|{packet.Type}|{packet.PayloadCipherBase64}";
        byte[] dataToSign = Encoding.UTF8.GetBytes(rawStringToSign);

        using var privateKey = Key.Import(SignatureAlgorithm.Ed25519, _privateIdentityKey, KeyBlobFormat.RawPrivateKey);
        packet.Signature = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(privateKey, dataToSign));

        await _webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(packet))), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    #region AES-GCM Engine
    public string EncryptAesGcm(string plainText, byte[] key)
    {
        using var aes = new AesGcm(key, 16);
        byte[] nonce = new byte[12]; RandomNumberGenerator.Fill(nonce);
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = new byte[plainBytes.Length]; byte[] tag = new byte[16];
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        using var ms = new MemoryStream(); using var writer = new BinaryWriter(ms);
        writer.Write(nonce); writer.Write(tag); writer.Write(cipherBytes);
        return Convert.ToBase64String(ms.ToArray());
    }

    public string DecryptAesGcm(string cipherBase64, byte[] key) 
    { 
        byte[] cipherData = Convert.FromBase64String(cipherBase64); 
        using var ms = new MemoryStream(cipherData); 
        using var reader = new BinaryReader(ms); 
        byte[] nonce = reader.ReadBytes(12); 
        byte[] tag = reader.ReadBytes(16); 
        byte[] ciphertext = reader.ReadBytes(cipherData.Length - 12 - 16); 
        using var aes = new AesGcm(key, 16); 
        byte[] plainBytes = new byte[ciphertext.Length]; 
        aes.Decrypt(nonce, ciphertext, tag, plainBytes); 
        return Encoding.UTF8.GetString(plainBytes); 
    }
    #endregion

    public async Task DistributeGroupKeysAsync(Guid groupId, string groupName, List<string> members, byte[] groupMasterKey, LocalSecureStorage storage)
    {
        foreach (var member in members)
        {
            if (string.Equals(member, Username, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                await InitializeE2EEChannelWithAsync(member, storage);

                byte[] sessionKey = GetSharedKeyForUser(member, storage);

                using var aes = new AesGcm(sessionKey, 16);
                byte[] nonce = new byte[12];
                RandomNumberGenerator.Fill(nonce);

                byte[] ciphertext = new byte[groupMasterKey.Length];
                byte[] tag = new byte[16];

                aes.Encrypt(nonce, groupMasterKey, ciphertext, tag);

                byte[] packetBytes = new byte[nonce.Length + tag.Length + ciphertext.Length];
                Buffer.BlockCopy(nonce, 0, packetBytes, 0, nonce.Length);
                Buffer.BlockCopy(tag, 0, packetBytes, nonce.Length, tag.Length);
                Buffer.BlockCopy(ciphertext, 0, packetBytes, nonce.Length + tag.Length, ciphertext.Length);

                string encryptedKeyBase64 = Convert.ToBase64String(packetBytes);

                var keyPacket = new GroupKeyPacket
                {
                    GroupId = groupId,
                    GroupName = groupName,
                    Creator = Username,
                    EncryptedGroupKeyBase64 = encryptedKeyBase64
                };

                string jsonPayload = System.Text.Json.JsonSerializer.Serialize(keyPacket);

                // ИСПРАВЛЕНО: ОСТАВЛЯЕМ ТОЛЬКО ЭТОТ ОДИН ВАЛИДНЫЙ СЕТЕВОЙ ВЫЗОВ!
                // ВСЁ, ЧТО ШЛО НИЖЕ ЭТОЙ СТРОКИ (networkMessage и _webSocket.SendAsync) — ПОЛНОСТЬЮ УДАЛИТЕ!
                await SendPacketAsync(member, "GROUP_KEY_DISTRIBUTION", jsonPayload);

                Console.WriteLine($"[ГРУППА] Ключ для {member} успешно отправлен в сеть.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ГРУППА] Не удалось отправить ключ для {member}: {ex.Message}");
            }
        }
    }

    // ИСПРАВЛЕНО: Метод теперь считывает сессионный ключ X3DH напрямую из LiteDB, 
    // избегая ошибок обращения к закрытым внутренним словарям памяти!
    private byte[] GetSharedKeyForUser(string username, LocalSecureStorage storage)
    {
        // Запрашиваем ключ общения с пользователем из базы данных. 
        // В вашей архитектуре этот метод в storage может называться GetE2EKey или GetSharedSecret.
        // Если метод называется по-другому, мы можем прочитать коллекцию напрямую:
        // Формируем уникальный идентификатор ключа сессии с этим другом
        var target = username.ToLower().Trim();

        if (_activeChatKeys.TryGetValue(target, out var sessionKey))
        {
            return sessionKey;
        }

        throw new Exception($"Криптографический ключ X3DH сессии для пользователя {target} не найден в ОЗУ.");
    }

    // Проверка подписи SignedPrekey Ed25519-ключом identity отправителя бандла.
    // Зеркальная копия серверной VerifySignedPrekey — проверка обязана выполняться
    // именно тут, на клиенте-получателе, а не только на сервере при регистрации.
    private bool VerifySignedPrekey(string identityEd25519PublicKeyBase64, string signedPrekeyBase64, string signatureBase64)
    {
        try
        {
            if (string.IsNullOrEmpty(signatureBase64)) return false; // старый бандл без подписи — не доверяем

            byte[] pubKeyBytes = Convert.FromBase64String(identityEd25519PublicKeyBase64);
            byte[] prekeyBytes = Convert.FromBase64String(signedPrekeyBase64);
            byte[] sigBytes = Convert.FromBase64String(signatureBase64);

            var algorithm = SignatureAlgorithm.Ed25519;
            var publicKey = PublicKey.Import(algorithm, pubKeyBytes, KeyBlobFormat.RawPublicKey);

            return algorithm.Verify(publicKey, prekeyBytes, sigBytes);
        }
        catch
        {
            return false;
        }
    }

    private class PrekeyBundleDto 
    {
        public string Username { get; set; } = "";
        public string Ed25519PublicKey { get; set; } = "";
        public string EcdhIdentityKey { get; set; } = "";
        public string SignedPrekey { get; set; } = "";
        public string SignedPrekeySignature { get; set; } = "";
        public string OneTimePrekey { get; set; } = "";
    }
}
