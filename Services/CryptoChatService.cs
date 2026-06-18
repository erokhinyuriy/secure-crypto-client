using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NSec.Cryptography;
using SecureCryptoClient.Models;

namespace SecureCryptoClient.Services;

public class CryptoChatService
{
    private readonly string _serverHttpUrl = "http://localhost:5267"; // Наш ASP.NET Core сервер
    private readonly string _serverWsUrl = "ws://localhost:5267/ws";
    private readonly HttpClient _httpClient = new();
    private ClientWebSocket? _webSocket;

    private byte[]? _privateIdentityKey;
    public string PublicKeyBase64 { get; private set; } = "";
    public string Username { get; set; } = "";

    // Фиксированный симметричный ключ (AES-GCM) для MVP-переписки.
    // В полной версии заменяется на Double Ratchet на базе ECDH
    private readonly byte[] _e2eeSharedKey = Encoding.UTF8.GetBytes("SuperSecret_E2EE_ChannelKey_32B!");

    public CryptoChatService(string username)
    {
        Username = username.ToLower().Trim();
    }

    // Шаг А: Регистрация нового аккаунта на сервере
    public async Task<bool> RegisterAsync(string username, LocalSecureStorage storage)
    {
        Username = username.ToLower().Trim();
        var algo = SignatureAlgorithm.Ed25519;

        // Генерируем постоянную пару ключей с политикой экспорта
        using var key = Key.Create(algo, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var privKeyBytes = key.Export(KeyBlobFormat.RawPrivateKey);
        var pubKeyBytes = key.Export(KeyBlobFormat.RawPublicKey);
        var pubKeyBase64 = Convert.ToBase64String(pubKeyBytes);

        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{_serverHttpUrl}/api/auth/register", new { Username = Username, PublicKeyBase64 = pubKeyBase64 });
            if (response.IsSuccessStatusCode)
            {
                // ЗАПОМИНАЕМ В ПАМЯТИ
                _privateIdentityKey = privKeyBytes;
                PublicKeyBase64 = pubKeyBase64;

                // ЖЕЛЕЗОБЕТОННЫЙ ФИКС: Сразу же намертво записываем ключи в зашифрованный сейф устройства!
                storage.SaveConfigValue("identity_private", Convert.ToBase64String(privKeyBytes));
                storage.SaveConfigValue("identity_public", pubKeyBase64);

                return true;
            }
        }
        catch { return false; }
        return false;
    }

    // Шаг Б: Подключение к WebSocket и запуск фонового прослушивания сообщений
    public async Task ConnectAsync(Action<ChatMessage> onMessageReceived)
    {
        if (_privateIdentityKey == null) throw new InvalidOperationException("Клиент не авторизован / нет ключей.");

        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(new Uri(_serverWsUrl), CancellationToken.None);

        // Верифицируем сессию на сервере первым PING-пакетом
        await SendPacketAsync("", "PING", "");

        // Фоновый цикл чтения сокета
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
                    var packet = JsonSerializer.Deserialize<SignedPacket>(json);

                    if (packet != null && packet.Type == "MESSAGE")
                    {
                        // Сквозная расшифровка контента силами AES-GCM
                        string decryptedText = DecryptAesGcm(packet.PayloadCipherBase64, _e2eeSharedKey);

                        var msg = new ChatMessage
                        {
                            ChatPartner = packet.Sender,
                            Sender = packet.Sender,
                            Text = decryptedText
                        };
                        onMessageReceived(msg);
                    }
                }
                catch { break; }
            }
        });
    }

    // Шаг В: Отправка сообщения
    public async Task SendMessageAsync(string recipient, string plainText)
    {
        // 1. Сквозное шифрование (Сервер не прочитает)
        string cipherBase64 = EncryptAesGcm(plainText, _e2eeSharedKey);

        // 2. Подпись Ed25519 и отправка
        await SendPacketAsync(recipient, "MESSAGE", cipherBase64);
    }

    private async Task SendPacketAsync(string recipient, string type, string cipherPayload)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open || _privateIdentityKey == null) return;

        var normalizedSender = Username.ToLower().Trim();
        var normalizedRecipient = recipient.ToLower().Trim();

        var packet = new SignedPacket
        {
            Sender = normalizedSender,
            Recipient = normalizedRecipient,
            Type = type,
            PayloadCipherBase64 = cipherPayload
        };

        // ИСПРАВЛЕНО: Собираем точно такой же анонимный объект, как на сервере
        var rawDataToSign = new
        {
            s = normalizedSender,
            r = normalizedRecipient,
            t = packet.Type,
            p = packet.PayloadCipherBase64
        };

        // Сериализуем его в точно такой же JSON-формат
        string jsonToSign = JsonSerializer.Serialize(rawDataToSign);
        byte[] dataToSign = Encoding.UTF8.GetBytes(jsonToSign);

        using var privateKey = Key.Import(SignatureAlgorithm.Ed25519, _privateIdentityKey, KeyBlobFormat.RawPrivateKey);
        byte[] signatureBytes = SignatureAlgorithm.Ed25519.Sign(privateKey, dataToSign);
        packet.Signature = Convert.ToBase64String(signatureBytes);

        // Отправляем полный пакет в сеть
        var json = JsonSerializer.Serialize(packet);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    // ВАЖНО: Этот метод будет вызываться при успешном логине, чтобы загрузить старые ключи из LiteDB!
    public bool LoadIdentityKeysFromStorage(LocalSecureStorage storage)
    {
        // Пробуем прочитать сохраненные ключи
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

    #region AES-GCM Engine
    private string EncryptAesGcm(string plainText, byte[] key)
    {
        using var aes = new AesGcm(key, 16);

        // ИСПРАВЛЕНО: Явно выделяем стандартные 12 байт для nonce
        byte[] nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = new byte[plainBytes.Length];
        byte[] tag = new byte[16];

        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(nonce);
        writer.Write(tag);
        writer.Write(cipherBytes);
        return Convert.ToBase64String(ms.ToArray());
    }

    private string DecryptAesGcm(string cipherBase64, byte[] key)
    {
        byte[] cipherData = Convert.FromBase64String(cipherBase64);
        using var ms = new MemoryStream(cipherData);
        using var reader = new BinaryReader(ms);

        // ИСПРАВЛЕНО: Считываем стандартные 12 байт nonce
        byte[] nonce = reader.ReadBytes(12);
        byte[] tag = reader.ReadBytes(16);
        byte[] ciphertext = reader.ReadBytes(cipherData.Length - 12 - 16);

        using var aes = new AesGcm(key, 16);
        byte[] plainBytes = new byte[ciphertext.Length];
        aes.Decrypt(nonce, ciphertext, tag, plainBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }
    #endregion
}
