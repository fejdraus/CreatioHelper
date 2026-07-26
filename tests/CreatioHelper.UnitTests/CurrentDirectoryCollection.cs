namespace CreatioHelper.Tests;

/// <summary>
/// Tests that switch the process-wide current directory must not run in parallel:
/// one test would otherwise read settings from the directory another one just moved into.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class CurrentDirectoryCollection
{
    public const string Name = "CurrentDirectory";
}
