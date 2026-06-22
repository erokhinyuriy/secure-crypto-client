using System;

namespace SecureCryptoClient.Models;

// Модель для сохранения ключа группы в локальной зашифрованной LiteDB
public class LocalGroupKeyInfo
{
    public string GroupIdStr { get; set; } = ""; // Ключ поиска (ID группы в строку)
    public string GroupName { get; set; } = "";
    public byte[] AESGroupKey { get; set; } = Array.Empty<byte>(); // Физический ключ шифрования чата
}
