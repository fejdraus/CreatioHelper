using System.Net.Http.Json;
using System.Net.Http.Headers;
using Blazored.LocalStorage;

namespace CreatioHelper.WebUI.Services;

/// <summary>
/// Authentication service for managing user login state
/// </summary>
public interface IAuthService
{
    bool IsAuthenticated { get; }
    string? Username { get; }
    string? Role { get; }
    string? Token { get; }
    string? ErrorMessage { get; }
    bool IsAdmin { get; }

    event Action<bool>? OnAuthStateChanged;

    Task<bool> LoginAsync(string username, string password);
    Task<bool> LoginWithApiKeyAsync(string apiKey);
    Task LogoutAsync();
    Task<bool> ValidateSessionAsync();
    Task InitializeAsync();
}

public class AuthService : IAuthService
{
    private const string AuthTokenKey = "authToken";
    private const string UsernameKey = "username";
    private const string TokenExpiresKey = "tokenExpires";
    private const string RoleKey = "userRole";

    private readonly HttpClient _httpClient;
    private readonly ILocalStorageService _localStorage;

    private bool _isAuthenticated;
    private string? _username;
    private string? _role;
    private string? _token;
    private string? _errorMessage;

    public bool IsAuthenticated => _isAuthenticated;
    public string? Username => _username;
    public string? Role => _role;
    public string? Token => _token;
    public string? ErrorMessage => _errorMessage;
    public bool IsAdmin => string.Equals(_role, "admin", StringComparison.OrdinalIgnoreCase);

    public event Action<bool>? OnAuthStateChanged;

    public AuthService(HttpClient httpClient, ILocalStorageService localStorage)
    {
        _httpClient = httpClient;
        _localStorage = localStorage;
    }

    public async Task InitializeAsync()
    {
        await ValidateSessionAsync();
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        _errorMessage = null;

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/auth/token", new
            {
                Username = username,
                Password = password
            });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<TokenResponse>();
                if (result?.Token != null)
                {
                    await _localStorage.SetItemAsync(AuthTokenKey, result.Token);
                    await _localStorage.SetItemAsync(UsernameKey, username);
                    await _localStorage.SetItemAsync(TokenExpiresKey, result.ExpiresAt);

                    SetAuthorizationHeader(result.Token);

                    // Fetch role from validate endpoint
                    var role = await FetchRoleAsync();

                    _isAuthenticated = true;
                    _username = username;
                    _role = role;
                    _token = result.Token;

                    await _localStorage.SetItemAsync(RoleKey, role ?? "user");

                    OnAuthStateChanged?.Invoke(true);
                    return true;
                }
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
                _errorMessage = error?.Message ?? "Too many login attempts. Please try again later.";
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _errorMessage = "Invalid username or password";
            }
            else
            {
                _errorMessage = $"Login failed: {response.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"Connection error: {ex.Message}";
        }

        return false;
    }

    public async Task<bool> LoginWithApiKeyAsync(string apiKey)
    {
        _errorMessage = null;

        try
        {
            SetAuthorizationHeader(apiKey);

            var response = await _httpClient.GetAsync("/rest/system/status");

            if (response.IsSuccessStatusCode)
            {
                await _localStorage.SetItemAsync(AuthTokenKey, apiKey);
                await _localStorage.SetItemAsync(UsernameKey, "API User");

                _isAuthenticated = true;
                _token = apiKey;
                _username = "API User";

                OnAuthStateChanged?.Invoke(true);
                return true;
            }

            _errorMessage = "Invalid API key";
        }
        catch (Exception ex)
        {
            _errorMessage = $"Connection error: {ex.Message}";
        }

        ClearAuthorizationHeader();
        return false;
    }

    public async Task LogoutAsync()
    {
        await _localStorage.RemoveItemAsync(AuthTokenKey);
        await _localStorage.RemoveItemAsync(UsernameKey);
        await _localStorage.RemoveItemAsync(TokenExpiresKey);
        await _localStorage.RemoveItemAsync(RoleKey);

        ClearAuthorizationHeader();

        _isAuthenticated = false;
        _username = null;
        _role = null;
        _token = null;
        _errorMessage = null;

        OnAuthStateChanged?.Invoke(false);
    }

    public async Task<bool> ValidateSessionAsync()
    {
        try
        {
            var token = await _localStorage.GetItemAsync<string>(AuthTokenKey);
            var username = await _localStorage.GetItemAsync<string>(UsernameKey);
            var expiresAt = await _localStorage.GetItemAsync<DateTime?>(TokenExpiresKey);

            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            // Check if token is expired
            if (expiresAt.HasValue && expiresAt.Value < DateTime.UtcNow)
            {
                await LogoutAsync();
                return false;
            }

            SetAuthorizationHeader(token);

            // Validate by making a request
            var response = await _httpClient.GetAsync("/api/auth/validate");

            if (response.IsSuccessStatusCode)
            {
                var validateResult = await response.Content.ReadFromJsonAsync<ValidateResponse>();
                var role = validateResult?.Role
                    ?? await _localStorage.GetItemAsync<string>(RoleKey)
                    ?? "user";

                _isAuthenticated = true;
                _username = validateResult?.Username ?? username ?? "User";
                _role = role;
                _token = token;

                await _localStorage.SetItemAsync(RoleKey, role);

                OnAuthStateChanged?.Invoke(true);
                return true;
            }
        }
        catch
        {
            // Session validation failed
        }

        ClearAuthorizationHeader();
        _isAuthenticated = false;
        return false;
    }

    private void SetAuthorizationHeader(string token)
    {
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
    }

    private void ClearAuthorizationHeader()
    {
        _httpClient.DefaultRequestHeaders.Authorization = null;
    }

    private async Task<string?> FetchRoleAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/auth/validate");
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ValidateResponse>();
                return result?.Role;
            }
        }
        catch
        {
            // Ignore - role will default
        }
        return null;
    }

    private class TokenResponse
    {
        public string? Token { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string? TokenType { get; set; }
    }

    private class ValidateResponse
    {
        public bool Valid { get; set; }
        public string? Username { get; set; }
        public string? Role { get; set; }
    }

    private class ErrorResponse
    {
        public string? Message { get; set; }
    }
}
