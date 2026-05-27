using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.ViewModels;

public partial class ScriptsViewModel : ObservableObject
{
    private readonly IOutputWriter _output;
    private readonly IWindowsFeaturesService _windowsFeatures;
    private readonly IModuleCleanupService _moduleCleanup;
    private readonly ITerrasoftSvnCleanupService _svnCleanup;
    private readonly Func<string?> _sitePathProvider;

    public event EventHandler? NavigateToLog;

    public ScriptsViewModel(
        IOutputWriter output,
        IWindowsFeaturesService windowsFeatures,
        IModuleCleanupService moduleCleanup,
        ITerrasoftSvnCleanupService svnCleanup,
        Func<string?> sitePathProvider)
    {
        _output = output;
        _windowsFeatures = windowsFeatures;
        _moduleCleanup = moduleCleanup;
        _svnCleanup = svnCleanup;
        _sitePathProvider = sitePathProvider;
    }

    [RelayCommand]
    private async Task RunWindowsFeaturesAsync()
    {
        _output.Clear();
        NavigateToLog?.Invoke(this, EventArgs.Empty);
        await _windowsFeatures.EnableCreatioFeaturesAsync();
    }

    [RelayCommand]
    private async Task RunModuleCleanupAsync()
    {
        var sitePath = _sitePathProvider();
        if (string.IsNullOrWhiteSpace(sitePath))
        {
            _output.WriteLine("[ERROR] Site path is not configured.");
            return;
        }

        _output.Clear();
        NavigateToLog?.Invoke(this, EventArgs.Empty);
        _output.WriteLine("[INFO] Removing redundant module records...");
        await _moduleCleanup.CleanupAsync(sitePath);
    }

    [RelayCommand]
    private async Task RunSvnCleanupAsync()
    {
        var sitePath = _sitePathProvider();
        if (string.IsNullOrWhiteSpace(sitePath))
        {
            _output.WriteLine("[ERROR] Site path is not configured.");
            return;
        }

        _output.Clear();
        NavigateToLog?.Invoke(this, EventArgs.Empty);
        _output.WriteLine("[INFO] Removing Terrasoft SVN settings from Crt* packages...");
        await Task.Run(() => _svnCleanup.CleanupAsync(sitePath));
    }
}
