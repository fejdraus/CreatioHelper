using System.Collections.Generic;
using System.Threading.Tasks;
using CreatioHelper.Core.Models;

namespace CreatioHelper.Core.Abstractions;

public interface IWebServerServiceFactory
{
    IWebServerService CreateWebServerService();
    bool IsWebServerSupported();
    Task<string> GetSupportedWebServerTypeAsync();
    List<string> GetAvailableWebServerTypes();
}