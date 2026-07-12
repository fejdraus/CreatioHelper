using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

public interface IConnectionStringsEditor
{
    ConnectionStringsData Read(string sitePath);
    void Write(string sitePath, ConnectionStringsData data);
}
