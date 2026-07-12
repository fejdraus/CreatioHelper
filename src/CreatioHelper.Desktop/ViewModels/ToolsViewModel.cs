using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.ViewModels;

public partial class ToolsViewModel : ObservableObject
{
    private readonly IWebConfigEditor _editor;
    private readonly Func<string?> _sitePathProvider;
    private WebConfigData? _loadedData;
    private string? _lastSitePath;
    private bool _isPopulating;

    private static readonly HashSet<string> _editableProperties = new()
    {
        nameof(ServerPath), nameof(DistinguishedName), nameof(CurrentSchemaName),
        nameof(UsePathThroughAuthentication), nameof(FileDesignMode), nameof(HttpsEncrypted),
        nameof(PortForClientConnection), nameof(EnableProxy), nameof(ProxyAddress),
        nameof(InternalUserPassword), nameof(SspUserPassword), nameof(Ldap),
        nameof(SspLdapProvider), nameof(SsoAuthProvider), nameof(SspSsoAuthProvider),
        nameof(QuartzEnabled),
    };

    [ObservableProperty] private string _serverPath = "";
    [ObservableProperty] private string _distinguishedName = "";
    [ObservableProperty] private string _currentSchemaName = "";
    [ObservableProperty] private bool _usePathThroughAuthentication;
    [ObservableProperty] private bool _fileDesignMode;
    [ObservableProperty] private bool _httpsEncrypted;
    [ObservableProperty] private int? _portForClientConnection = 443;
    [ObservableProperty] private bool _enableProxy;
    [ObservableProperty] private string _proxyAddress = "";
    [ObservableProperty] private bool _isConfigLoaded;
    [ObservableProperty] private bool _isNetCore;

    public event EventHandler<string>? SaveFailed;

    [ObservableProperty] private bool _quartzEnabled;

    // Auth providers
    [ObservableProperty] private bool _internalUserPassword;
    [ObservableProperty] private bool _sspUserPassword;
    [ObservableProperty] private bool _ldap;
    [ObservableProperty] private bool _sspLdapProvider;
    [ObservableProperty] private bool _ssoAuthProvider;
    [ObservableProperty] private bool _sspSsoAuthProvider;

    public ToolsViewModel(IWebConfigEditor editor, Func<string?> sitePathProvider)
    {
        _editor = editor;
        _sitePathProvider = sitePathProvider;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loadedData is not null && !_isPopulating && e.PropertyName is not null
            && _editableProperties.Contains(e.PropertyName))
        {
            SaveConfig();
        }
    }

    [RelayCommand]
    internal void LoadConfig()
    {
        var sitePath = _sitePathProvider();
        if (string.IsNullOrWhiteSpace(sitePath))
        {
            IsConfigLoaded = false;
            return;
        }

        try
        {
            _isPopulating = true;
            _loadedData = _editor.Read(sitePath);
            _lastSitePath = sitePath;
            PopulateFromData(_loadedData);
            IsConfigLoaded = true;
        }
        catch
        {
            IsConfigLoaded = false;
        }
        finally
        {
            _isPopulating = false;
        }
    }

    private void SaveConfig()
    {
        if (_lastSitePath is null) return;
        try
        {
            var data = BuildData();
            _editor.Write(_lastSitePath, data);
            _loadedData = data;
        }
        catch (Exception ex)
        {
            SaveFailed?.Invoke(this, ex.Message);
        }
    }

    private void PopulateFromData(WebConfigData data)
    {
        IsNetCore = data.IsNetCore;
        ServerPath = data.ServerPath;
        DistinguishedName = data.DistinguishedName;
        CurrentSchemaName = data.CurrentSchemaName;
        // As per original: checkbox reflects FileDesignMode=true AND UseStaticFileContent=false
        FileDesignMode = data.FileDesignMode && !data.UseStaticFileContent;
        UsePathThroughAuthentication = data.UsePathThroughAuthentication;
        HttpsEncrypted = data.HttpsEncrypted;
        PortForClientConnection = data.PortForClientConnection;
        EnableProxy = data.EnableProxy;
        ProxyAddress = data.ProxyAddress;
        QuartzEnabled = data.QuartzEnabled;

        var providers = data.ProviderNames
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        InternalUserPassword = providers.Contains("InternalUserPassword");
        SspUserPassword = providers.Contains("SSPUserPassword");
        Ldap = data.IsNetCore
            ? providers.Contains("LdapNtlmProviderOnWindows") || providers.Contains("LdapProvider")
            : providers.Contains("Ldap");
        SspLdapProvider = providers.Contains("SSPLdapProvider");
        SsoAuthProvider = providers.Contains("SsoAuthProvider");
        SspSsoAuthProvider = providers.Contains("SSPSsoAuthProvider");
    }

    private WebConfigData BuildData()
    {
        var isCore = _loadedData!.IsNetCore;

        string autoLoginProviderNames;
        if (isCore)
        {
            // Preserve as-is for .NET Core (different auth mechanism)
            autoLoginProviderNames = _loadedData.AutoLoginProviderNames;
        }
        else
        {
            var autoLogin = new HashSet<string>();
            if (Ldap) { autoLogin.Add("NtlmUser"); }
            if (SspLdapProvider) { autoLogin.Add("NtlmUser"); autoLogin.Add("SSPNtlmUser"); }
            autoLoginProviderNames = string.Join(",", autoLogin);
        }

        return new WebConfigData
        {
            IsNetCore = isCore,
            ServerPath = ServerPath,
            DistinguishedName = DistinguishedName,
            ProviderNames = string.Join(",", new[]
            {
                InternalUserPassword                 ? "InternalUserPassword"      : null,
                SspUserPassword                      ? "SSPUserPassword"           : null,
                Ldap && !isCore                      ? "Ldap"                      : null,
                Ldap && isCore                       ? "LdapNtlmProviderOnWindows" : null,
                Ldap && isCore                       ? "LdapProvider"              : null,
                SspLdapProvider                      ? "SSPLdapProvider"           : null,
                SsoAuthProvider                      ? "SsoAuthProvider"           : null,
                SspSsoAuthProvider                   ? "SSPSsoAuthProvider"        : null,
            }.Where(x => x is not null)),
            AutoLoginProviderNames = autoLoginProviderNames,
            CurrentSchemaName = CurrentSchemaName,
            FileDesignMode = FileDesignMode,
            UseStaticFileContent = !FileDesignMode,
            UsePathThroughAuthentication = UsePathThroughAuthentication,
            HttpsEncrypted = HttpsEncrypted,
            // Port is 0 when HTTPS is disabled (matches original behaviour)
            PortForClientConnection = HttpsEncrypted ? PortForClientConnection ?? 443 : 0,
            EnableProxy = EnableProxy,
            ProxyAddress = ProxyAddress,
            QuartzEnabled = QuartzEnabled,
            BehaviorsConfigSource = _loadedData.BehaviorsConfigSource,
            BindingsConfigSource = _loadedData.BindingsConfigSource,
            AppServicesSource = _loadedData.AppServicesSource,
        };
    }
}
