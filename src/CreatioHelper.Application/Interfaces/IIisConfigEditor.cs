namespace CreatioHelper.Application.Interfaces;

public interface IIisConfigEditor
{
    void SetPhysicalPath(string siteName, string physicalPath);
    void SetAppPool(string siteName, string poolName);
}
