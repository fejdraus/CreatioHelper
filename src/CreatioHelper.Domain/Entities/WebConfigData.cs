namespace CreatioHelper.Domain.Entities;

public class WebConfigData
{
    public string ServerPath { get; set; } = "";
    public string DistinguishedName { get; set; } = "";
    public string ProviderNames { get; set; } = "";
    public string AutoLoginProviderNames { get; set; } = "";
    public string CurrentSchemaName { get; set; } = "";
    public bool FileDesignMode { get; set; }
    public bool UseStaticFileContent { get; set; }
    public bool UsePathThroughAuthentication { get; set; }
    public bool HttpsEncrypted { get; set; }
    public int PortForClientConnection { get; set; } = 443;
    public bool EnableProxy { get; set; }
    public string ProxyAddress { get; set; } = "";

    // Preserved from read, needed for correct write-back
    public string BehaviorsConfigSource { get; set; } = "";
    public string BindingsConfigSource { get; set; } = "";
    public string AppServicesSource { get; set; } = "";

    public bool QuartzEnabled { get; set; }
    public bool IsNetCore { get; set; }
}
