namespace CreatioHelper.Application.Interfaces;

public interface ISiteConfigEditor
{
    void UpdateConnectionString(string configPath, string name, string connectionString);
}
