using System;

namespace CreatioHelper.Core
{
    public class ConsoleOutputWriter : IOutputWriter
    {
        public void WriteLine(string message) => Console.WriteLine(message);
    }
}