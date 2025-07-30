using System.ComponentModel;
using System.Runtime.CompilerServices;
using CreatioHelper.Domain.ValueObjects;

namespace CreatioHelper.Domain.Entities;

public class DtoServerInfo : INotifyPropertyChanged
{
    private string? _name;
    private string? _networkPath;
    private string? _poolName;
    private string? _siteName;
    

    public string? Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string? NetworkPath
    {
        get => _networkPath;
        set => SetField(ref _networkPath, value);
    }

    public string? PoolName
    {
        get => _poolName;
        set => SetField(ref _poolName, value);
    }

    public string? SiteName
    {
        get => _siteName;
        set => SetField(ref _siteName, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public override string ToString() => Name ?? "Unnamed Server";

    public override bool Equals(object? obj)
    {
        return obj is DtoServerInfo other && 
               string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return Name?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0;
    }
}
