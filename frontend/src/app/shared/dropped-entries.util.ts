export interface DroppedEntry {
  file: File;
  relativePath: string;
}

/** Files picked via a plain `<input type="file" multiple>`. */
export function collectFilesFromFileList(fileList: FileList): DroppedEntry[] {
  return Array.from(fileList).map((file) => ({ file, relativePath: file.webkitRelativePath || file.name }));
}

/**
 * Files/folders dropped from the OS onto the page. `dataTransfer.files` alone loses folder structure, so this
 * walks the (Chromium/Firefox) `DataTransferItem.webkitGetAsEntry()` tree instead, falling back to the flat file
 * list if that API isn't available.
 */
export async function collectFilesFromDataTransfer(dataTransfer: DataTransfer): Promise<DroppedEntry[]> {
  const items = Array.from(dataTransfer.items);
  const entries = items
    .filter((item) => item.kind === 'file')
    .map((item) => item.webkitGetAsEntry?.())
    .filter((entry): entry is FileSystemEntry => !!entry);

  if (entries.length === 0) {
    return collectFilesFromFileList(dataTransfer.files);
  }

  const results: DroppedEntry[] = [];
  await Promise.all(entries.map((entry) => walkEntry(entry, '', results)));
  return results;
}

async function walkEntry(entry: FileSystemEntry, prefix: string, out: DroppedEntry[]): Promise<void> {
  if (entry.isFile) {
    const file = await new Promise<File>((resolve, reject) => (entry as FileSystemFileEntry).file(resolve, reject));
    out.push({ file, relativePath: prefix ? `${prefix}/${entry.name}` : entry.name });
    return;
  }

  if (entry.isDirectory) {
    const reader = (entry as FileSystemDirectoryEntry).createReader();
    const childPrefix = prefix ? `${prefix}/${entry.name}` : entry.name;
    // readEntries() only returns a batch at a time (spec quirk) - keep calling it until it returns empty.
    let batch: FileSystemEntry[];
    do {
      batch = await new Promise<FileSystemEntry[]>((resolve, reject) => reader.readEntries(resolve, reject));
      await Promise.all(batch.map((child) => walkEntry(child, childPrefix, out)));
    } while (batch.length > 0);
  }
}
