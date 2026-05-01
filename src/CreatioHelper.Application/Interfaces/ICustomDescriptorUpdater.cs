namespace CreatioHelper.Application.Interfaces;

public interface ICustomDescriptorUpdater
{
    int RemoveDependencies(string sitePath, string packageNamesList);
}
