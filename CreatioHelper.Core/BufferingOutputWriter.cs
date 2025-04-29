using System;
using System.Collections.Generic;
using System.Timers;

namespace CreatioHelper.Core
{
    public class BufferingOutputWriter : IOutputWriter, IDisposable
    {
        private readonly Action<string> _writeAction;
        private readonly object _lock = new();
        private readonly List<string> _buffer = new();
        private readonly int _bufferSize;
        private readonly Timer _flushTimer;

        public BufferingOutputWriter(Action<string> writeAction, int bufferSize = 20)
        {
            _writeAction = writeAction ?? throw new ArgumentNullException(nameof(writeAction));
            _bufferSize = bufferSize;
            _flushTimer = new Timer(500);
            _flushTimer.Elapsed += (_, _) => FlushBuffer();
            _flushTimer.Start();
        }

        public void WriteLine(string line)
        {
            lock (_lock)
            {
                _buffer.Add(line);
                if (_buffer.Count >= _bufferSize)
                    FlushBuffer();
            }
        }

        private void FlushBuffer()
        {
            lock (_lock)
            {
                if (_buffer.Count == 0)
                    return;

                var messages = _buffer.ToArray();
                _buffer.Clear();
                _writeAction(string.Join(Environment.NewLine, messages));
            }
        }

        public void Dispose()
        {
            _flushTimer.Stop();
            _flushTimer.Dispose();
            FlushBuffer();
        }
    }
}