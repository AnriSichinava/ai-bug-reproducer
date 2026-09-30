namespace BugReproducer.Api.Dtos;

public record GenerateTestResponse(
    int GeneratedTestId,
    string FileName,
    string SourceCode,
    int TestRunId,
    bool Passed,
    string Output);
