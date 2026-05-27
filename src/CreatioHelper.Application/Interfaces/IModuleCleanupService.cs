using System.Threading.Tasks;

namespace CreatioHelper.Application.Interfaces;

public interface IModuleCleanupService
{
    Task<bool> CleanupAsync(string sitePath);
}
