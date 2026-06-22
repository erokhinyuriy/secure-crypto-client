using LiteDB;
using SecureCryptoClient.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SecureCryptoClient.Services;

public class LocalSecureStorage
{
    private readonly string _dbPath;
    private LiteDatabase? _database;
    private const int Iterations = 600_000; // Стандарт высокой надежности против брутфорса

    public LocalSecureStorage(string username)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appDir = Path.Combine(appData, "SecureCryptoMessenger");
        Directory.CreateDirectory(appDir);

        // ИМЯ ФАЙЛА ДОЛЖНО СТРОГО СОДЕРЖАТЬ ИМЯ ВОШЕДШЕГО ЮЗЕРА
        _dbPath = Path.Combine(appDir, $"store_{username.ToLower().Trim()}.db");
    }

    // Инициализация базы данных на основе введенного пользователем мастер-пароля
    public bool Initialize(string masterPassword)
    {
        try
        {
            var saltPath = _dbPath + ".salt";
            byte[] salt;

            if (!File.Exists(saltPath))
            {
                // Если запускаемся впервые — генерируем случайную соль и сохраняем её открыто
                salt = new byte[16];
                RandomNumberGenerator.Fill(salt);
                File.WriteAllBytes(saltPath, salt);
            }
            else
            {
                // Если база уже существует — читаем старую соль
                salt = File.ReadAllBytes(saltPath);
            }

            // Превращаем пользовательский пароль в мощный 256-битный AES-ключ
            byte[] passwordBytes = Encoding.UTF8.GetBytes(masterPassword);
            byte[] dbKeyBytes = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, Iterations, HashAlgorithmName.SHA256, 32);

            // Превращаем байты ключа в hex-строку для LiteDB
            string dbKeyHex = Convert.ToHexString(dbKeyBytes);

            // Открываем базу данных. Опция Password активирует аппаратное AES-256 шифрование файла
            _database = new LiteDatabase($"Filename={_dbPath};Password={dbKeyHex};");

            // Тестовый запрос, чтобы проверить, подошел ли пароль
            var testCollection = _database.GetCollection<ChatMessage>("messages");
            testCollection.EnsureIndex(x => x.ChatPartner);

            return true; // База успешно открыта и расшифрована в памяти!
        }
        catch
        {
            _database = null;
            return false; // Неверный пароль или файл базы данных поврежден
        }
    }

    // Сохранение входящего/исходящего сообщения в локальную зашифрованную историю
    public void SaveMessage(ChatMessage message)
    {
        if (_database == null) throw new InvalidOperationException("База данных не инициализирована.");

        var collection = _database.GetCollection<ChatMessage>("messages");
        collection.Insert(message);
    }

    // Получение истории переписки с конкретным другом
    public IEnumerable<ChatMessage> GetChatHistory(string chatPartner)
    {
        if (_database == null) throw new InvalidOperationException("База данных не инициализирована.");

        var collection = _database.GetCollection<ChatMessage>("messages");
        var partnerNormalized = chatPartner.ToLower().Trim();

        // СТРОГАЯ ФИЛЬТРАЦИЯ: возвращаем только те сообщения, где ChatPartner равен нужному другу!
        return collection.Find(x => x.ChatPartner == partnerNormalized);
    }

    // Метод для записи любой строки-настройки (например, ключа)
    public void SaveConfigValue(string key, string value)
    {
        if (_database == null) throw new InvalidOperationException("База данных не инициализирована.");
        var collection = _database.GetCollection<BsonDocument>("config");

        var doc = new BsonDocument { ["_id"] = key, ["Value"] = value };
        collection.Upsert(doc); // Обновит, если есть, или создаст новый
    }

    // Метод для чтения настройки
    public string? GetConfigValue(string key)
    {
        if (_database == null) throw new InvalidOperationException("База данных не инициализирована.");
        var collection = _database.GetCollection<BsonDocument>("config");

        var doc = collection.FindById(key);
        return doc?["Value"].AsString;
    }

    // Метод сканирует всю локальную историю сообщений и возвращает список уникальных имен собеседников
    public IEnumerable<string> GetUniqueChatPartners()
    {
        if (_database == null) throw new InvalidOperationException("База данных не инициализирована.");

        var collection = _database.GetCollection<ChatMessage>("messages");

        // Вытаскиваем только поле ChatPartner из всех сообщений и убираем дубликаты
        var allPartners = collection.FindAll()
                                    .Select(x => x.ChatPartner.ToLower().Trim())
                                    .Distinct();

        return allPartners;
    }

    #region Groups

    // Метод сохранения ключа группы
    public void SaveGroupKey(Guid groupId, string groupName, byte[] key)
    {
        var col = _database!.GetCollection<LocalGroupKeyInfo>("group_keys");
        var idStr = groupId.ToString().ToLower().Trim();

        // Проверяем, нет ли уже такого ключа
        var existing = col.FindOne(x => x.GroupIdStr == idStr);
        if (existing == null)
        {
            col.Insert(new LocalGroupKeyInfo { GroupIdStr = idStr, GroupName = groupName, AESGroupKey = key });
        }
    }

    // Метод чтения ключа группы
    public byte[]? GetGroupKey(Guid groupId)
    {
        var col = _database!.GetCollection<LocalGroupKeyInfo>("group_keys");
        var idStr = groupId.ToString().ToLower().Trim();
        return col.FindOne(x => x.GroupIdStr == idStr)?.AESGroupKey;
    }

    #endregion

    // Метод генерирует уникальный 256-битный ключ, привязанный к текущему железу/ОС
    private static byte[] GetMachineFingerprintKey()
    {
        // Собираем уникальные строки текущего компьютера (имя ПК и имя юзера ОС)
        string rawId = Environment.MachineName + Environment.UserName + Environment.ProcessorCount;
        // Превращаем этот уникальный шум в жесткий 32-байтный ключ через SHA256
        return SHA256.HashData(Encoding.UTF8.GetBytes(rawId));
    }

    // Метод шифрует пароль силами ОС под текущего пользователя и сохраняет в AppData
    public void SaveSecureAutologinToken(string username, string password)
    {
        try
        {
            var tokenPath = Path.Combine(Path.GetDirectoryName(_dbPath)!, $"autologin_{username.ToLower().Trim()}.dat");
            byte[] key = GetMachineFingerprintKey();
            byte[] plaintextBytes = Encoding.UTF8.GetBytes(password);

            using var aes = new AesGcm(key, 16);
            byte[] nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            byte[] ciphertext = new byte[plaintextBytes.Length];
            byte[] tag = new byte[16];

            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

            // Записываем в файл: Nonce (12B) + Tag (16B) + Ciphertext
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(nonce);
            writer.Write(tag);
            writer.Write(ciphertext);

            File.WriteAllBytes(tokenPath, ms.ToArray());
        }
        catch { }
    }

    // Статический метод для автоматической проверки: есть ли сохраненный токен в системе?
    public static string? TryGetSavedPassword(string username)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var tokenPath = Path.Combine(appData, "SecureCryptoMessenger", $"autologin_{username.ToLower().Trim()}.dat");

            if (!File.Exists(tokenPath)) return null;

            byte[] fileData = File.ReadAllBytes(tokenPath);
            using var ms = new MemoryStream(fileData);
            using var reader = new BinaryReader(ms);

            byte[] nonce = reader.ReadBytes(12);
            byte[] tag = reader.ReadBytes(16);
            byte[] ciphertext = reader.ReadBytes(fileData.Length - 12 - 16);

            byte[] key = GetMachineFingerprintKey();
            using var aes = new AesGcm(key, 16);
            byte[] decryptedBytes = new byte[ciphertext.Length];

            aes.Decrypt(nonce, ciphertext, tag, decryptedBytes);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch
        {
            return null; // Файл поврежден или перенесен на другой ПК — сбрасываем
        }
    }

    // Запоминаем имя последнего успешного пользователя в открытый JSON
    public static void SaveLastUser(string username)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var configPath = Path.Combine(appData, "SecureCryptoMessenger", "settings.json");

            string json = $"{{\"LastLoggedUser\": \"{username.ToLower().Trim()}\"}}";
            File.WriteAllText(configPath, json);
        }
        catch { }
    }

    // Читаем, кто заходил последним
    public static string? GetLastUser()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var configPath = Path.Combine(appData, "SecureCryptoMessenger", "settings.json");

            if (!File.Exists(configPath)) return null;

            string json = File.ReadAllText(configPath);
            // Простой парсинг строки без тяжелых JSON библиотек для MVP
            var parts = json.Split('"');
            if (parts.Length >= 4) return parts[3]; // Возвращает значение из "LastLoggedUser"
        }
        catch { }
        return null;
    }

    public void ClearChatHistory(string partnerName)
    {
        if (_database is null) 
            return;

        // Получаем доступ к вашей стандартной коллекции сообщений
        var col = _database.GetCollection<ChatMessage>("messages");

        var normalizedPartner = partnerName.ToLower().Trim();

        // Удаляем из базы все сообщения, где этот пользователь был либо отправителем, либо получателем
        col.DeleteMany(m => m.ChatPartner.ToLower() == normalizedPartner || m.Sender.ToLower() == normalizedPartner);
    }

    public byte[]? GetGroupKeyByName(string groupName)
    {
        if (_database is null) return null;

        var col = _database.GetCollection<LocalGroupKeyInfo>("group_keys");

        // ИСПРАВЛЕНО: Безопасно вычищаем префикс "Группа:" прямо внутри хранилища
        var normalizedName = groupName.Replace("Группа:", "").Replace("Группа", "").ToLower().Trim();

        var keyInfo = col.FindOne(x => x.GroupName.ToLower() == normalizedName);
        return keyInfo?.AESGroupKey;
    }

    // Закрытие базы данных при выходе из приложения (чтобы стереть ключи из ОЗУ)
    public void Close()
    {
        _database?.Dispose();
        _database = null;
    }
}
