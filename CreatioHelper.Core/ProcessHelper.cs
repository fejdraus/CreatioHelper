using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace CreatioHelper.Core
{
    public static class ProcessHelper
    {
        public static void Run(string exePath, string arguments, IOutputWriter output)
        {
            if (string.IsNullOrEmpty(exePath)) throw new ArgumentNullException(nameof(exePath));
            ArgumentNullException.ThrowIfNull(arguments);
            ArgumentNullException.ThrowIfNull(output);

            Run(exePath, arguments, output, Path.GetDirectoryName(exePath));
        }

        public static void Run(string exePath, string arguments, IOutputWriter output, string workingDirectory)
        {
            if (string.IsNullOrEmpty(exePath)) throw new ArgumentNullException(nameof(exePath));
            ArgumentNullException.ThrowIfNull(arguments);
            ArgumentNullException.ThrowIfNull(output);

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.GetEncoding(866),
                StandardErrorEncoding = Encoding.GetEncoding(866)
            };

            using var process = new Process();
            process.StartInfo = startInfo;
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    output.WriteLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    output.WriteLine($"[ERROR] {e.Data}");
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
        }

        public static async Task<int> RunAsync(string exePath, string arguments, IOutputWriter output, string? workingDirectory = null)
        {
            if (string.IsNullOrEmpty(exePath)) throw new ArgumentNullException(nameof(exePath));
            ArgumentNullException.ThrowIfNull(arguments);
            ArgumentNullException.ThrowIfNull(output);

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.GetEncoding(866),
                StandardErrorEncoding = Encoding.GetEncoding(866)
            };

            using var process = new Process();
            process.StartInfo = startInfo;
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    output.WriteLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    output.WriteLine($"[ERROR] {e.Data}");
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await Task.Run(() => process.WaitForExit());
            await Task.Delay(200);
            return process.ExitCode;
        }
    }
}