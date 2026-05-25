using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

public interface IWebConfigEditor
{
    WebConfigData Read(string sitePath);
    void Write(string sitePath, WebConfigData data);
}
