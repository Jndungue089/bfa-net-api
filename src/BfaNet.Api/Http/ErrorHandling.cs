using BfaNet.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BfaNet.Api.Http;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        ProblemDetails problem;
        switch (ex)
        {
            case AppException app:
                problem = new ProblemDetails { Status = app.Status, Title = app.Message };
                problem.Extensions["code"] = app.Code;
                if (app.Errors is not null) problem.Extensions["errors"] = app.Errors;
                break;
            case BadHttpRequestException:
            case System.Text.Json.JsonException:
                problem = new ProblemDetails { Status = 400, Title = "Pedido inválido." };
                problem.Extensions["code"] = ErrorCodes.Validation;
                break;
            case OperationCanceledException:
                return true; // client went away
            default:
                // Full detail goes to the log only; the client never sees internals.
                log.LogError(ex, "Erro não tratado em {Method} {Path}", http.Request.Method, http.Request.Path);
                problem = new ProblemDetails { Status = 500, Title = "Ocorreu um erro inesperado. Tente novamente." };
                problem.Extensions["code"] = "internal_error";
                break;
        }

        problem.Extensions["traceId"] = http.TraceIdentifier;
        http.Response.StatusCode = problem.Status!.Value;
        await http.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
