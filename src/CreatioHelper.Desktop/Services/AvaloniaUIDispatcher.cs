using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CreatioHelper.Application.Interfaces;

namespace CreatioHelper.Services;

/// <summary>
/// Avalonia implementation of UI thread dispatcher
/// </summary>
public class AvaloniaUIDispatcher : IUIDispatcher
{
    public async Task InvokeAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(action);
        }
    }

    public async Task<T> InvokeAsync<T>(Func<T> function)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return function();
        }
        else
        {
            return await Dispatcher.UIThread.InvokeAsync(function);
        }
    }

    public async Task InvokeAsync(Func<Task> asyncFunction)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            await asyncFunction();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(asyncFunction);
        }
    }

    public async Task<T> InvokeAsync<T>(Func<Task<T>> asyncFunction)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return await asyncFunction();
        }
        else
        {
            return await Dispatcher.UIThread.InvokeAsync(asyncFunction);
        }
    }
}