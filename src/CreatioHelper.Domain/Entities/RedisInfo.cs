namespace CreatioHelper.Domain.Entities;

public class RedisInfo
{
    public string[]? Hosts { get; set; }
    public string? DataBase { get; set; }
    public string? Password { get; set; }
    public bool UseTls { get; set; } = false;
    public string CertificatePath { get; set; } = string.Empty;
    public string CertificatePassword { get; set; } = string.Empty;
}