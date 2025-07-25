using System.Threading.Tasks;

namespace CreatioHelper.Services;

public interface IDialogService
{
    Task<string?> OpenFolderPickerAsync(string title);
}
