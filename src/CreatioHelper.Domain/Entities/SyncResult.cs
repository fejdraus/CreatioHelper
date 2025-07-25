using System;
namespace CreatioHelper.Domain.Entities;

public class SyncResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public long BytesTransferred { get; set; }
    public TimeSpan Duration { get; set; }
}