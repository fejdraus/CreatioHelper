using System.Collections.Generic;
namespace CreatioHelper.Core.Models;

public class SyncOptions
{
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public bool OverwriteExisting { get; set; } = true;
    public List<string> ExcludePatterns { get; set; } = new();
    public bool Recursive { get; set; } = true;
}