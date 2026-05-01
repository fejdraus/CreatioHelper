using System.Collections.Generic;
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

    public async Task<string?> OpenFilePickerAsync(string title, string[]? filters = null)
    {
        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        };

        if (filters != null && filters.Length > 0)
        {
            options.FileTypeFilter = new List<FilePickerFileType>
            {
                new("Allowed files") { Patterns = filters }
            };
        }

        var files = await _storageProvider.OpenFilePickerAsync(options);

        if (files.Any())
        {
            return files.First().Path.LocalPath;
        }
        return null;
    }

    public async Task<string?> SaveFilePickerAsync(string title, string? defaultFileName = null, string[]? filters = null)
    {
        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultFileName
        };

        if (filters != null && filters.Length > 0)
        {
            options.FileTypeChoices = new List<FilePickerFileType>
            {
                new("Allowed files") { Patterns = filters }
            };
        }

        var file = await _storageProvider.SaveFilePickerAsync(options);
        return file?.Path.LocalPath;
    }
}
