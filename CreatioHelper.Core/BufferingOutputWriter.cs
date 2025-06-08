using System;
using System.Collections.Generic;
using System.Timers;

namespace CreatioHelper.Core
{
    public class BufferingOutputWriter : IOutputWriter
    {
        private readonly Action<string> _writeAction;

        public BufferingOutputWriter(Action<string> writeAction)
        {
            _writeAction = writeAction ?? throw new ArgumentNullException(nameof(writeAction));
        }

        public void WriteLine(string line)
        {
            _writeAction(string.Concat(DateTime.Now, " ", line));
        }
    }
}