using CreatioHelper.WebUI.Resources;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace CreatioHelper.WebUI.MudLocalization;

public class CreatioMudLocalizer : MudLocalizer
{
    private readonly IStringLocalizer<Localization> _localizer;

    public CreatioMudLocalizer(IStringLocalizer<Localization> localizer)
    {
        _localizer = localizer;
    }

    public override LocalizedString this[string key]
    {
        get
        {
            var translated = _localizer[key];
            return translated.ResourceNotFound
                ? new LocalizedString(key, key, resourceNotFound: true)
                : translated;
        }
    }
}
