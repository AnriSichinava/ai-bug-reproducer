using System.ComponentModel.DataAnnotations;

namespace BugReproducer.Api.Dtos;

public record CreateBugReportRequest(
    [Required] int ProjectId,
    [Required, MaxLength(200)] string Title,
    [Required, MaxLength(4000)] string Description);

public record BugReportResponse(int Id, int ProjectId, string Title, string Description, DateTime CreatedAtUtc);
