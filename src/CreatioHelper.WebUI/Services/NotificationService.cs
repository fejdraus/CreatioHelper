using Microsoft.JSInterop;

namespace CreatioHelper.WebUI.Services;

public interface INotificationService
{
    Task<string> GetPermissionAsync();
    Task<string> RequestPermissionAsync();
    Task<bool> ShowAsync(string title, string body, string? tag = null);
}

public class NotificationService : INotificationService
{
    private readonly IJSRuntime _js;

    public NotificationService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<string> GetPermissionAsync()
    {
        try
        {
            return await _js.InvokeAsync<string>("creatioNotifications.permission");
        }
        catch (JSException)
        {
            return "unsupported";
        }
    }

    public async Task<string> RequestPermissionAsync()
    {
        try
        {
            return await _js.InvokeAsync<string>("creatioNotifications.requestPermission");
        }
        catch (JSException)
        {
            return "denied";
        }
    }

    public async Task<bool> ShowAsync(string title, string body, string? tag = null)
    {
        try
        {
            return await _js.InvokeAsync<bool>("creatioNotifications.show", title, body, tag);
        }
        catch (JSException)
        {
            return false;
        }
    }
}
