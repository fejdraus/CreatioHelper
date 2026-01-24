using System.Text;

namespace CreatioHelper.Infrastructure.Services.FileSystem;

public static class UnicodeNormalizer
{
    public static string NormalizeToNfc(string path) => path.Normalize(NormalizationForm.FormC);
    public static string NormalizeToNfd(string path) => path.Normalize(NormalizationForm.FormD);
    public static bool NeedsNormalization(string path) => path != NormalizeToNfc(path);
}
