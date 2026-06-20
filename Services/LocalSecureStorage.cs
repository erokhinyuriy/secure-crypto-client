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

    // Закрытие базы данных при выходе из приложения (чтобы стереть ключи из ОЗУ)
    public void Close()
    {
        _database?.Dispose();
        _database = null;
    }
}
