namespace CreatioHelper.Agent.Services;

public class DiscoveredSite
{
    public string SiteName { get; set; } = "";
    public string AppRootPath { get; set; } = "";
    public List<DiscoveredFolder> Folders { get; set; } = new();
}

public class DiscoveredFolder
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public class SiteControlInfo
{
    public string ServiceName { get; set; } = "";
    public string AppPool { get; set; } = "";

    // Site and pool run independently in IIS, so they are reported separately.
    public string SiteState { get; set; } = "";
    public string PoolState { get; set; } = "";

    public bool IsNested { get; set; }
    public bool PoolShared { get; set; }

    // Top-level sites are always manageable (their pool is dedicated); a nested application
    // may only be controlled if its app pool is not shared with other sites/applications.
    public bool CanManage => !(IsNested && PoolShared);
}
