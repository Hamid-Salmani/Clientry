using System.Net.Mail;
using ClientHub.Api.Controllers;
using ClientHub.Domain;
using FluentValidation;

namespace ClientHub.Api.Validators;

public sealed class BillingDetailsValidator : AbstractValidator<ClientBillingController.BillingDetailsRequest>
{
    public BillingDetailsValidator()
    {
        RuleFor(request => request.LegalName).MaximumLength(160);
        RuleFor(request => request.TaxId).MaximumLength(80);
        RuleFor(request => request.BillingAddress).MaximumLength(1000);
        RuleFor(request => request.BillingEmails).MaximumLength(500).Must(IsValidBillingEmails)
            .WithMessage("Enter up to five valid billing email addresses.");
    }

    private static bool IsValidBillingEmails(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var emails = value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return emails.Length <= 5 && emails.All(email =>
            MailAddress.TryCreate(email, out var address) &&
            string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class ClientTeamInvitationValidator : AbstractValidator<ClientTeamController.InvitationRequest>
{
    public ClientTeamInvitationValidator()
    {
        RuleFor(request => request.FullName).NotEmpty().MaximumLength(120);
        RuleFor(request => request.Email).NotEmpty().MaximumLength(254).Must(IsValidEmail)
            .WithMessage("Enter a valid email address.");
    }

    private static bool IsValidEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email) && MailAddress.TryCreate(email.Trim(), out var address) &&
        string.Equals(address.Address, email.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed class ApprovalDecisionValidator : AbstractValidator<ApprovalsController.ApprovalDecisionRequest>
{
    public ApprovalDecisionValidator()
    {
        RuleFor(request => request.Decision).Must(value =>
            Enum.TryParse<MilestoneApprovalStatus>(value, true, out var parsed) &&
            parsed is MilestoneApprovalStatus.Approved or MilestoneApprovalStatus.ChangesRequested)
            .WithMessage("Choose Approved or ChangesRequested.");
        RuleFor(request => request.Note).MaximumLength(2000);
        RuleFor(request => request).Must(request =>
            !string.Equals(request.Decision, nameof(MilestoneApprovalStatus.ChangesRequested), StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(request.Note))
            .WithMessage("A note is required when requesting changes.");
    }
}

public sealed class ProjectCommentValidator : AbstractValidator<ProjectsController.AddCommentRequest>
{
    public ProjectCommentValidator()
    {
        RuleFor(request => request.Body).NotEmpty().MaximumLength(5000);
    }
}

public sealed class ProjectRequestDecisionValidator : AbstractValidator<ProjectRequestsController.AdminDecisionRequest>
{
    public ProjectRequestDecisionValidator()
    {
        RuleFor(request => request.Status).Must(value =>
            Enum.TryParse<ProjectRequestStatus>(value, true, out var parsed) &&
            parsed is ProjectRequestStatus.Accepted or ProjectRequestStatus.Declined)
            .WithMessage("Choose Accepted or Declined.");
        RuleFor(request => request.Note).Must(note => !string.IsNullOrWhiteSpace(note)).MaximumLength(2000)
            .WithMessage("A response of at most 2,000 characters is required.");
    }
}

public sealed class RegisterValidator : AbstractValidator<AuthController.RegisterRequest>
{
    public RegisterValidator()
    {
        RuleFor(request => request.Email).NotEmpty().MaximumLength(254).EmailAddress();
        RuleFor(request => request.Password).NotEmpty().MinimumLength(8);
        RuleFor(request => request.FullName).NotEmpty().MaximumLength(120);
    }
}

public sealed class ChangePasswordValidator : AbstractValidator<AuthController.ChangePasswordRequest>
{
    public ChangePasswordValidator()
    {
        RuleFor(request => request.CurrentPassword).NotEmpty();
        RuleFor(request => request.NewPassword).NotEmpty().MinimumLength(8);
    }
}

public sealed class UpdateProfileValidator : AbstractValidator<AuthController.UpdateProfileRequest>
{
    public UpdateProfileValidator()
    {
        RuleFor(request => request.FullName).NotEmpty().MaximumLength(120);
    }
}
