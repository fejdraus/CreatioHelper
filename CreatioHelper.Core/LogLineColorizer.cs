using System;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace CreatioHelper.Core;

public class LogLineColorizer : DocumentColorizingTransformer
{
    private static readonly IBrush WarningForeground = Brushes.DarkOrange;
    private static readonly IBrush ErrorForeground   = Brushes.Red;

    protected override void ColorizeLine(DocumentLine line)
    {
        var text = CurrentContext.Document.GetText(line);
        if (text.IndexOf("[WARNING]", StringComparison.OrdinalIgnoreCase) >= 0 
            || text.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            ChangeLinePart(
                line.Offset, 
                line.EndOffset,
                element => {
                    element.TextRunProperties.SetForegroundBrush(WarningForeground);
                });
        }
        else if (text.IndexOf("[ERROR]", StringComparison.OrdinalIgnoreCase) >= 0
                 || text.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            ChangeLinePart(
                line.Offset, 
                line.EndOffset,
                element => {
                    element.TextRunProperties.SetForegroundBrush(ErrorForeground);
                });
        }
    }
}