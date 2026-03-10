using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CreatioHelper.Domain.Entities;
using CreatioHelper.ViewModels;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Services;

public interface IOperationsService : INotifyPropertyChanged
{
    Task StartOperation(MainWindowViewModel viewModel);
    void StopOperation();
    bool IsBusy { get; }
    string StartButtonText { get; }
    bool IsStopButtonEnabled { get; }
    ISyncthingMonitorService? GetSyncthingMonitor();

    /// <summary>
    /// Execute a standalone WSC operation with full UI blocking and stop support.
    /// </summary>
    Task ExecuteWscOperationAsync(string sitePath, string operationName, Func<int> action);

    /// <summary>
    /// Start all IIS sites and application pools for servers in the list
    /// </summary>
    Task StartAllIisAsync(IEnumerable<ServerInfo> servers);

    /// <summary>
    /// Stop all IIS sites and application pools for servers in the list
    /// </summary>
    Task StopAllIisAsync(IEnumerable<ServerInfo> servers);
}
