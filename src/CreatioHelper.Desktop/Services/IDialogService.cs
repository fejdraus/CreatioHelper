using System.Threading.Tasks;

namespace CreatioHelper.Services;

public interface IDialogService
{
    Task<string?> OpenFolderPickerAsync(string title);
    Task<string?> OpenFilePickerAsync(string title, string[]? filters = null);
    Task<string?> SaveFilePickerAsync(string title, string? defaultFileName = null, string[]? filters = null);
}
