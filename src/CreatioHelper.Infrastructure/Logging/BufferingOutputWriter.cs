using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Infrastructure.Logging
{
    public class BufferingOutputWriter : IOutputWriter
    {
        private readonly Action<string> _writeAction;
        private readonly Action _clearAction;

        public BufferingOutputWriter(Action<string> writeAction, Action? clearAction = null)
        {
            _writeAction = writeAction ?? throw new ArgumentNullException(nameof(writeAction));
            _clearAction = clearAction ?? (() => { });
        }

        public void WriteLine(string line)
        {
            _writeAction(string.Concat(DateTime.Now, " ", line));
        }

        public void Clear()
        {
            _clearAction();
        }
    }
}