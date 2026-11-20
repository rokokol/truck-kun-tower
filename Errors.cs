using Microsoft.AspNetCore.Mvc;

// Services say what went wrong and with which status; controllers stay free of error plumbing.
public sealed class AppError(string message, int status, string? code = null) : Exception(message) {
    public int Status { get; } = status;
    public string? Code { get; } = code;
    public static AppError BadRequest(string message) => new(message, 400);
    public static AppError Unauthorized(string message) => new(message, 401);
    public static AppError Forbidden(string message) => new(message, 403);
    public static AppError NotFound(string message) => new(message, 404);
    public static AppError Conflict(string message, string? code = null) => new(message, 409, code);
}

public static class ErrorHandling {
    public static IApplicationBuilder UseAppErrors(this IApplicationBuilder app) => app.Use(async (context, next) => {
        try {
            await next();
        } catch (AppError error) {
            context.Response.StatusCode = error.Status;
            var details = new ProblemDetails {
                Status = error.Status,
                Title = error.Status switch {
                    400 => "Некорректный запрос",
                    401 => "Нужен вход",
                    403 => "Нет доступа",
                    404 => "Не найдено",
                    409 => "Конфликт",
                    _ => "Ошибка"
                },
                Detail = error.Message
            };
            if (error.Code is { } code) details.Extensions["code"] = code;
            await context.Response.WriteAsJsonAsync(details);
        }
    });
}
