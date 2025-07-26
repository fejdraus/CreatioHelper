using System;
using CreatioHelper.Application.Interfaces;

namespace CreatioHelper.Infrastructure.Services;

public class ConsoleOutputWriter : IOutputWriter
{
    public void WriteLine(string message) => Console.WriteLine(message);
    public void Clear() => Console.Clear();
}
