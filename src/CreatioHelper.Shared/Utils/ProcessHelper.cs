#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Shared.Utils
{
    public static class ProcessHelper
    {
        private static readonly Job Job = new();
        
        public static int Run(string exePath, string arguments, IOutputWriter output)
        {
            var workingDirectory = Path.GetDirectoryName(exePath);
            return Run(exePath, arguments, output, workingDirectory);
        }

        public static int Run(string exePath, string arguments, IOutputWriter output, string? workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(exePath)) 
                throw new ArgumentNullException(nameof(exePath));
            ArgumentNullException.ThrowIfNull(arguments);
            ArgumentNullException.ThrowIfNull(output);
            var process = StartAndReturn(exePath, arguments, output, workingDirectory);
            Job.AddProcess(process.Handle);
            process.WaitForExit();
            return process.ExitCode;
        }

        private static Process StartAndReturn(string exePath, string arguments, IOutputWriter output, string? workingDirectory = null)
        {
            var process = new Process();
            var encoding = OperatingSystem.IsWindows() 
                ? Encoding.GetEncoding("cp866")
                : Encoding.UTF8;
            process.StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding  = encoding
            };
            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Task.Run(() => output.WriteLine(e.Data));
                }
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Task.Run(() => output.WriteLine($"[ERROR] {e.Data}"));
                }
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
    
            return process;
        }
    }
}
