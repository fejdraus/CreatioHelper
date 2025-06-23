namespace CreatioHelper.Agent.Models;

public class SyncResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public long BytesTransferred { get; set; }
    public TimeSpan Duration { get; set; }
}