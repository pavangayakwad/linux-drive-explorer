using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace FileExplorer.Api.Filters;

/// <summary>
/// Without this, ASP.NET Core's default value provider factories (FormValueProviderFactory,
/// FormFileValueProviderFactory, JQueryFormValueProviderFactory) eagerly call Request.ReadFormAsync() while
/// setting up model binding for ANY action parameter - even one that doesn't bind from the body, like a plain
/// CancellationToken - whenever the request's content type is multipart/form-data. That fully consumes the
/// request body before the action ever runs, so a manual MultipartReader inside the action then fails with
/// "Unexpected end of Stream, the content may have already been read by another component." Apply this to any
/// action that reads the multipart body itself instead of relying on [FromForm]/IFormFile binding.
/// </summary>
public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        factories.RemoveType<FormValueProviderFactory>();
        factories.RemoveType<FormFileValueProviderFactory>();
        factories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
