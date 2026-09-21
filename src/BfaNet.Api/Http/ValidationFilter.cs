using BfaNet.Application.Common;
using FluentValidation;

namespace BfaNet.Api.Http;

/// <summary>Validates the bound argument of type <typeparamref name="T"/> and short-circuits with 422 + field errors.</summary>
public sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var model = ctx.Arguments.OfType<T>().FirstOrDefault()
            ?? throw new AppException(ErrorCodes.Validation, "Corpo do pedido em falta.", 400);

        var result = await validator.ValidateAsync(model, ctx.HttpContext.RequestAborted);
        if (!result.IsValid)
        {
            var errors = result.Errors
                .GroupBy(e => ToCamel(e.PropertyName))
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
            throw new AppException(ErrorCodes.Validation, "Dados inválidos.", 422, errors);
        }
        return await next(ctx);
    }

    private static string ToCamel(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s[1..];
}

public static class ValidationExtensions
{
    public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder b) where T : class =>
        b.AddEndpointFilter<ValidationFilter<T>>();
}
