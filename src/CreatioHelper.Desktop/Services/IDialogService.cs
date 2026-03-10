using System.Threading.Tasks;

namespace CreatioHelper.Services;

public interface IDialogService
{
    Task<string?> OpenFolderPickerAsync(string title);
    Task<string?> OpenFilePickerAsync(string title, string[]? filters = null);
}
