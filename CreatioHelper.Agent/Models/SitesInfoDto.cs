namespace CreatioHelper.Agent.Models;

public class SitesInfoDto
{
    public int Total { get; set; }
    public int Running { get; set; }
    public int Stopped { get; set; }
    public List<SiteDetailDto> Details { get; set; } = new();
}