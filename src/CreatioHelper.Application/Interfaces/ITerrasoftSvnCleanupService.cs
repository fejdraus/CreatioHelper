using System.Threading.Tasks;

namespace CreatioHelper.Application.Interfaces;

public interface ITerrasoftSvnCleanupService
{
    Task<bool> CleanupAsync(string sitePath);
}
