using System.Net.Http;

namespace CreatioHelper.Services;

public sealed class SyncthingRequestFactory
{
    private readonly string _apiUrl;
    private readonly string? _apiKey;
    public SyncthingRequestFactory(string apiUrl, string? apiKey)
    {
        _apiUrl = (apiUrl ?? string.Empty).TrimEnd('/');
        _apiKey = apiKey;
    }
    public string BuildUrl(string relativeUrl) => $"{_apiUrl}{relativeUrl}";
    public HttpRequestMessage Create(HttpMethod method, string relativeUrl)
    {
        var request = new HttpRequestMessage(method, BuildUrl(relativeUrl));
        if (!string.IsNullOrEmpty(_apiKey))
        {
            request.Headers.Add("X-API-Key", _apiKey);
        }
        return request;
    }
    public HttpRequestMessage Get(string relativeUrl) => Create(HttpMethod.Get, relativeUrl);
}