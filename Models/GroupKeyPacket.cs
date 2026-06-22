using System;
using System.Collections.Generic;
using System.Text;

namespace SecureCryptoClient.Models;

public class GroupKeyPacket
{
    public Guid GroupId { get; set; }
    public string GroupName { get; set; } = "";

    // Кто создал группу и прислал ключ
    public string Creator { get; set; } = "";

    // Зашифрованный через X3DH (AES-GCM) ключ группы в формате Base64.
    // Каждый участник получит свой уникальный зашифрованный кусок, 
    // который сможет прочитать только его приватная Identity-ключ пара!
    public string EncryptedGroupKeyBase64 { get; set; } = "";
}
