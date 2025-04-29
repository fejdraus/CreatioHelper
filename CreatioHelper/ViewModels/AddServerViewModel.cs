using System.ComponentModel;
using System.Runtime.CompilerServices;
using CreatioHelper.Core;

namespace CreatioHelper.ViewModels;

public class AddServerViewModel : INotifyPropertyChanged
{
    public ServerInfo Server { get; }

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
        get => Server.SiteName;
        set { Server.SiteName = value; OnPropertyChanged(); }
    }

    public string PoolName
    {
        get => Server.PoolName;
        set { Server.PoolName = value; OnPropertyChanged(); }
    }

    public AddServerViewModel(ServerInfo? server = null)
    {
        Server = server ?? new ServerInfo();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}