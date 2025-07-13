using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CreatioHelper.Core
{
    public sealed class ServerInfo : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _networkPath = string.Empty;
        private string _siteName = string.Empty;
        private string _poolName = string.Empty;
        private Version _appVersion = new();
        private string _poolStatus = "Unknown";
        private string _siteStatus = "Unknown";
        private bool _isStatusLoading;
        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                }
            }
        }

        public string NetworkPath
        {
            get => _networkPath;
            set
            {
                if (_networkPath != value)
                {
                    _networkPath = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SiteName
        {
            get => _siteName;
            set
            {
                if (_siteName != value)
                {
                    _siteName = value;
                    OnPropertyChanged();
                }
            }
        }

        public string PoolName
        {
            get => _poolName;
            set
            {
                if (_poolName != value)
                {
                    _poolName = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public string PoolStatus
        {
            get => _poolStatus;
            set
            {
                if (_poolStatus != value)
                {
                    _poolStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SiteStatus
        {
            get => _siteStatus;
            set
            {
                if (_siteStatus != value)
                {
                    _siteStatus = value;
                    OnPropertyChanged();
                }
            }
        }
        
        public Version AppVersion
        {
            get => _appVersion;
            set
            {
                if (_appVersion != value)
                {
                    _appVersion = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsStatusLoading
        {
            get => _isStatusLoading;
            set
            {
                if (_isStatusLoading != value)
                {
                    _isStatusLoading = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}