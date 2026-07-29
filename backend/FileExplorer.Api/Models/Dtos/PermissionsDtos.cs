namespace FileExplorer.Api.Models.Dtos;

public record PermissionsDto(string Owner, string Group, string OctalMode, string SymbolicMode, bool Supported);

public record UpdatePermissionsRequest(string Path, string? OctalMode, string? Owner, string? Group);

public record PrincipalDto(string Name, string Id);

public record PrincipalsResponse(IReadOnlyList<PrincipalDto> Users, IReadOnlyList<PrincipalDto> Groups);
