using Avalonia.Threading;
using System;

namespace CreatioHelper.Services
{
    public class LogService
    {
        private readonly Action<string> _logAction;

        public LogService(Action<string> logAction)
        {
            _logAction = logAction;
        }

        public void WriteLog(string line)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                _logAction(line);
            }
            else
            {
                Dispatcher.UIThread.Post(() => _logAction(line));
            }
        }
    }
}
