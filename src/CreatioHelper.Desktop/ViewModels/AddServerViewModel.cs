using System.ComponentModel;
using System.Runtime.CompilerServices;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.ValueObjects;

namespace CreatioHelper.ViewModels;

public class AddServerViewModel(ServerInfo? server = null) : INotifyPropertyChanged
{
    public ServerInfo Server { get; } = server ?? new ServerInfo();

    public string ServerName
    {
        get => Server.Name?.Value ?? string.Empty;
        set { Server.Name = string.IsNullOrEmpty(value) ? null : new ServerName(value); OnPropertyChanged(); }
    }

    public string NetworkPath
    {
        get => Server.NetworkPath?.Value ?? string.Empty;
        set { Server.NetworkPath = string.IsNullOrEmpty(value) ? null : new NetworkPath(value); OnPropertyChanged(); }
    }

    public string SiteName
    {
        get => Server.SiteName ?? string.Empty;
        set { Server.SiteName = value; OnPropertyChanged(); }
    }

    public string PoolName
    {
        get => Server.PoolName ?? string.Empty;
        set { Server.PoolName = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public bool Validate(out string? validationError)
    {
        if (string.IsNullOrWhiteSpace(ServerName))
        {
            validationError = "Please enter the server name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(NetworkPath))
        {
            validationError = "Please enter the network path.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SiteName))
        {
            validationError = "Please enter the site name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(PoolName))
        {
            validationError = "Please enter the pool name.";
            return false;
        }

        validationError = null;
        return true;
    }
}