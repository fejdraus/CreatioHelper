namespace CreatioHelper.Contracts.Responses;

public class WebServerResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public Data? Data { get; set; }
}
