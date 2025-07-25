using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace CreatioHelper.Services;

public class DialogService : IDialogService
{
    private readonly IStorageProvider _storageProvider;

    public DialogService(IStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
    }

    public async Task<string?> OpenFolderPickerAsync(string title)
    {
        var folders = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        if (folders.Any())
        {
            return folders.First().Path.LocalPath;
        }
        return null;
    }
}
