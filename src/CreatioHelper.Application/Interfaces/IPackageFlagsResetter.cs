namespace CreatioHelper.Application.Interfaces;

public interface IPackageFlagsResetter
{
    bool ResetFlags(string sitePath, bool includeUnlockedPackages);
}
