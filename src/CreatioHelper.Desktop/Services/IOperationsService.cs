using System.ComponentModel;
using System.Threading.Tasks;
using CreatioHelper.ViewModels;

namespace CreatioHelper.Services;

public interface IOperationsService : INotifyPropertyChanged
{
    Task StartOperation(MainWindowViewModel viewModel);
    void StopOperation();
    bool IsBusy { get; }
    string StartButtonText { get; }
    bool IsStopButtonEnabled { get; }
}
