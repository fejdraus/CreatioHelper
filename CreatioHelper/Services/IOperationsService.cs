using System.Threading.Tasks;
using CreatioHelper.Core;
using CreatioHelper.ViewModels;

namespace CreatioHelper.Services;

public interface IOperationsService
{
    Task StartOperation(MainWindowViewModel viewModel);
    void StopOperation();
    bool IsBusy { get; }
    string StartButtonText { get; }
    bool IsStopButtonEnabled { get; }
}
