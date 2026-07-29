using FileExplorer.Api.Models.Dtos;

namespace FileExplorer.Api.Services;

public interface IPermissionsService
{
    Task<PermissionsDto> GetAsync(string virtualPath, CancellationToken ct = default);

    Task UpdateAsync(string virtualPath, string? octalMode, string? owner, string? group, CancellationToken ct = default);

    Task<PrincipalsResponse> ListPrincipalsAsync(CancellationToken ct = default);
}
