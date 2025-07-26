using System.ComponentModel;
using System.Runtime.CompilerServices;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.ViewModels;

public class AddServerViewModel(ServerInfo? server = null) : INotifyPropertyChanged
{
    public ServerInfo Server { get; } = server ?? new ServerInfo();

    public string ServerName
    {
        get => Server.Name;
        set { Server.Name = value; OnPropertyChanged(); }
    }

    public string NetworkPath
    {
        get => Server.NetworkPath;
        set { Server.NetworkPath = value; OnPropertyChanged(); }
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
}