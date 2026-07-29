using FileExplorer.Api.Models.Dtos;

namespace FileExplorer.Api.Services;

public interface IFileSystemService
{
    DirectoryListingDto ListDirectory(string virtualPath);

    FileEntryDto CreateEntry(string parentVirtualPath, string name, bool isDirectory);

    FileEntryDto Rename(string virtualPath, string newName);

    IReadOnlyList<FileEntryDto> Search(string rootVirtualPath, string query, int maxResults = 200);
}
