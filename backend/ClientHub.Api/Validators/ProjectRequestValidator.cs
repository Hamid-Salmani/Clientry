using ClientHub.Api.Controllers;
using FluentValidation;

namespace ClientHub.Api.Validators;

public sealed class ProjectRequestValidator : AbstractValidator<ProjectRequestsController.CreateRequest>
{
    public ProjectRequestValidator()
    {
        RuleFor(request => request.Title).NotEmpty().MaximumLength(160);
        RuleFor(request => request.Description).NotEmpty().MaximumLength(4000);
        RuleFor(request => request.Priority).Must(value =>
            Enum.TryParse<ClientHub.Domain.ProjectRequestPriority>(value, true, out var parsed) &&
            Enum.IsDefined(parsed));
    }
}
