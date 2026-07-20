using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

public interface IWebConfigEditor
{
    WebConfigData Read(string sitePath);
    void Write(string sitePath, WebConfigData data);
    bool? ReadRetryRedisOperation(string sitePath);
    void WriteRetryRedisOperation(string sitePath, bool enabled);
    IReadOnlyList<KeyValuePair<string, string>>? ReadRedisSection(string sitePath);
    void WriteRedisSection(string sitePath, IReadOnlyList<KeyValuePair<string, string>> attributes);
}
