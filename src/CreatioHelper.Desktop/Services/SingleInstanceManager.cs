using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CreatioHelper.Services;

public class SingleInstanceManager : IDisposable
{
    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private bool _isOwner;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _listenerTask;

    public event EventHandler? ActivationRequested;

    public SingleInstanceManager(string appId)
    {
        _mutexName = $"Global\\{appId}";
        _pipeName = $"{appId}_Pipe";
    }

    /// <summary>
    /// Tries to acquire the single instance lock.
    /// </summary>
    /// <returns>
    /// True if this is the first instance (lock acquired),
    /// False if another instance is already running
    /// </returns>
    public bool TryAcquireLock()
    {
        try
        {
            bool createdNew;
            _mutex = new Mutex(true, _mutexName, out createdNew);

            if (createdNew)
            {
                // This is the first instance
                _isOwner = true;
                StartPipeServer();
                return true;
            }

            // Try to wait for mutex with zero timeout to check if we can acquire it
            bool hasHandle = false;
            try
            {
                hasHandle = _mutex.WaitOne(0, false);
                if (hasHandle)
                {
                    // We got the mutex, meaning previous instance died without cleanup
                    _isOwner = true;
                    StartPipeServer();
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                // Previous owner crashed, we now own the mutex
                _isOwner = true;
                StartPipeServer();
                return true;
            }
            finally
            {
                if (!_isOwner && hasHandle)
                {
                    _mutex.ReleaseMutex();
                }
            }

            // Another instance is running
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Mutex exists but we don't have access
            return false;
        }
    }

    public void NotifyFirstInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(1000); // 1 second timeout

            var message = Encoding.UTF8.GetBytes("ACTIVATE");
            client.Write(message, 0, message.Length);
            client.Flush();
        }
        catch
        {
            // If we can't connect to pipe, the first instance might be closing
        }
    }

    private void StartPipeServer()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenForActivationRequests(_cancellationTokenSource.Token));
    }

    private async Task ListenForActivationRequests(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken);

                var buffer = new byte[1024];
                int bytesRead = await server.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                if (bytesRead > 0)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    if (message == "ACTIVATE")
                    {
                        ActivationRequested?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue listening even if one connection fails
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _listenerTask?.Wait(TimeSpan.FromSeconds(2));
        _cancellationTokenSource?.Dispose();

        if (_isOwner && _mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }

        _mutex?.Dispose();
    }
}
