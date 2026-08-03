using FileExplorer.Api.Models.Entities;

namespace FileExplorer.Api.Services;

/// <summary>
/// Result of a MoveToTrash attempt. When the item's mount doesn't have enough free space to hold a
/// trashed copy, the item is deleted permanently instead - <see cref="PermanentlyDeleted"/> is set and
/// <see cref="Item"/> is null.
/// </summary>
public sealed class TrashOutcome
{
    public required string Name { get; init; }
    public bool PermanentlyDeleted { get; init; }
    public TrashItem? Item { get; init; }
}

public interface ITrashService
{
    /// <summary>
    /// Moves the item at the given virtual path into its mount's hidden trash directory. If there isn't
    /// enough free space on that mount to do so, the item is deleted permanently instead.
    /// </summary>
    TrashOutcome MoveToTrash(string virtualOriginalPath, int userId);

    /// <summary>Deletes the item at the given virtual path outright, bypassing Trash entirely.</summary>
    TrashOutcome DeleteForever(string virtualOriginalPath);

    /// <summary>
    /// Checks whether every mount involved has enough free space to hold trashed copies of all the given
    /// items at once (items are grouped and summed per mount, since trashing doesn't free any space up as
    /// it goes). Used to ask the user up front whether a delete that can't be trashed should proceed as a
    /// permanent delete instead.
    /// </summary>
    bool HasSpaceForAll(IReadOnlyList<string> virtualPaths);

    /// <summary>Moves a trashed item back to its original location, resolving name conflicts if needed.</summary>
    void Restore(TrashItem item);

    string ToPhysicalTrashPath(TrashItem item);
}
