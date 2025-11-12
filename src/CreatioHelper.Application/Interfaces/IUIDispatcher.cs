namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Dispatcher for UI thread operations
/// </summary>
public interface IUIDispatcher
{
    /// <summary>
    /// Invoke an action on the UI thread
    /// </summary>
    Task InvokeAsync(Action action);

    /// <summary>
    /// Invoke a function on the UI thread and return result
    /// </summary>
    Task<T> InvokeAsync<T>(Func<T> function);

    /// <summary>
    /// Invoke an async function on the UI thread
    /// </summary>
    Task InvokeAsync(Func<Task> asyncFunction);

    /// <summary>
    /// Invoke an async function on the UI thread and return result
    /// </summary>
    Task<T> InvokeAsync<T>(Func<Task<T>> asyncFunction);
}