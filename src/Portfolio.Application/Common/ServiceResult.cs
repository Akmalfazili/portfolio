namespace Portfolio.Application.Common;

public enum ServiceErrorKind
{
    NotFound,
    Validation,
}

/// <summary>
/// Non-exceptional failure for an application service call. Endpoints map <see cref="Kind"/>
/// onto the appropriate HTTP typed result (404 or a 400 <c>ValidationProblem</c>).
/// </summary>
public sealed class ServiceError
{
    private ServiceError(ServiceErrorKind kind, IDictionary<string, string[]>? validationErrors)
    {
        Kind = kind;
        ValidationErrors = validationErrors;
    }

    public ServiceErrorKind Kind { get; }

    /// <summary>Field -> messages. Only populated when <see cref="Kind"/> is <see cref="ServiceErrorKind.Validation"/>.</summary>
    public IDictionary<string, string[]>? ValidationErrors { get; }

    public static ServiceError NotFound() => new(ServiceErrorKind.NotFound, null);

    public static ServiceError Validation(IDictionary<string, string[]> errors) =>
        new(ServiceErrorKind.Validation, errors);
}

/// <summary>Result of an application service call that can fail without throwing.</summary>
public readonly struct ServiceResult<T>
{
    private ServiceResult(T? value, ServiceError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public ServiceError? Error { get; }

    public bool IsSuccess => Error is null;

    public static ServiceResult<T> Success(T value) => new(value, null);

    public static ServiceResult<T> Failure(ServiceError error) => new(default, error);
}
