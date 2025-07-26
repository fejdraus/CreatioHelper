using AvaloniaEdit;

namespace CreatioHelper.Converters;

public class UpdateLogDisplay
{
    public void ScrollToBottom(bool isAutoScrollEnabled, bool isWrapTextEnabled, TextEditor logTextEditor)
    {
        if (isAutoScrollEnabled)
        {
            var doc = logTextEditor.Document;
            int lastLineNumber = doc.LineCount;
            var lastLine = doc.GetLineByNumber(lastLineNumber);
            logTextEditor.CaretOffset = isWrapTextEnabled ? lastLine.EndOffset : lastLine.Offset;
            logTextEditor.TextArea.Caret.BringCaretToView();
        }
    }
}