namespace CreatioHelper.Domain.Entities;

public class AppPoolsInfoDto
{
    public int Total { get; set; }
    public int Running { get; set; }
    public int Stopped { get; set; }
    public List<AppPoolDetailDto> Details { get; set; } = new();
}