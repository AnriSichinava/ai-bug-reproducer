using System.ComponentModel.DataAnnotations;

namespace BugReproducer.Api.Dtos;

public record CreateProjectRequest([Required, MaxLength(200)] string Name);

public record ProjectResponse(int Id, string Name, DateTime CreatedAtUtc);
