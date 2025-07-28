namespace CreatioHelper.Domain.Common;

public class Result<T>
{
    public bool IsSuccess { get; private set; }
    public T? Value { get; private set; }
    public string ErrorMessage { get; private set; }
    public Exception? Exception { get; private set; }

    private Result(bool isSuccess, T? value, string errorMessage, Exception? exception = null)
    {
        IsSuccess = isSuccess;
        Value = value;
        ErrorMessage = errorMessage;
        Exception = exception;
    }

    public static Result<T> Success(T value) => new(true, value, string.Empty);
    public static Result<T> Failure(string error, Exception? exception = null) => new(false, default, error, exception);
}

public class Result
{
    public bool IsSuccess { get; private set; }
    public string ErrorMessage { get; private set; }
    public Exception? Exception { get; private set; }

    private Result(bool isSuccess, string errorMessage, Exception? exception = null)
    {
        IsSuccess = isSuccess;
        ErrorMessage = errorMessage;
        Exception = exception;
    }

    public static Result Success() => new(true, string.Empty);
    public static Result Failure(string error, Exception? exception = null) => new(false, error, exception);
}
