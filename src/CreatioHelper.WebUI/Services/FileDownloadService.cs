using Microsoft.JSInterop;

namespace CreatioHelper.WebUI.Services;

public interface IFileDownloadService
{
    Task SaveTextAsync(string fileName, string content, string contentType = "text/plain;charset=utf-8");
}

public class FileDownloadService : IFileDownloadService
{
    private readonly IJSRuntime _js;

    public FileDownloadService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task SaveTextAsync(string fileName, string content, string contentType = "text/plain;charset=utf-8")
    {
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        await _js.InvokeVoidAsync("creatioDownload.saveFile", fileName, contentType, base64);
    }
}
