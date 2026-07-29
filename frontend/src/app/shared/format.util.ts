export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined) {
    return '';
  }
  if (bytes < 1024) {
    return `${bytes.toLocaleString()} B`;
  }
  const units = ['KB', 'MB', 'GB', 'TB', 'PB'];
  let value = bytes / 1024;
  let unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex++;
  }
  const formatted =
    value >= 100
      ? Math.round(value).toLocaleString()
      : value.toLocaleString(undefined, { maximumFractionDigits: value >= 10 ? 1 : 2 });
  return `${formatted} ${units[unitIndex]}`;
}

export function formatDateTime(iso: string): string {
  const date = new Date(iso);
  const dd = String(date.getDate()).padStart(2, '0');
  const mm = String(date.getMonth() + 1).padStart(2, '0');
  const yyyy = date.getFullYear();
  let hours = date.getHours();
  const minutes = String(date.getMinutes()).padStart(2, '0');
  const ampm = hours >= 12 ? 'PM' : 'AM';
  hours = hours % 12 || 12;
  return `${dd}-${mm}-${yyyy} ${String(hours).padStart(2, '0')}:${minutes} ${ampm}`;
}

export function pathSegments(path: string): { label: string; path: string }[] {
  if (path === '/' || path === '') {
    return [{ label: 'Root', path: '/' }];
  }
  const parts = path.split('/').filter(Boolean);
  const segments: { label: string; path: string }[] = [{ label: 'Root', path: '/' }];
  let accumulated = '';
  for (const part of parts) {
    accumulated += `/${part}`;
    segments.push({ label: part, path: accumulated });
  }
  return segments;
}

export function fileIconClass(isDirectory: boolean, extension: string | null): string {
  if (isDirectory) {
    return 'pi pi-folder file-icon file-icon--folder';
  }
  const ext = (extension ?? '').toLowerCase();
  const map: Record<string, string> = {
    '.jpg': 'pi pi-image',
    '.jpeg': 'pi pi-image',
    '.png': 'pi pi-image',
    '.gif': 'pi pi-image',
    '.svg': 'pi pi-image',
    '.pdf': 'pi pi-file-pdf',
    '.doc': 'pi pi-file-word',
    '.docx': 'pi pi-file-word',
    '.xls': 'pi pi-file-excel',
    '.xlsx': 'pi pi-file-excel',
    '.ppt': 'pi pi-file-word',
    '.pptx': 'pi pi-file-word',
    '.zip': 'pi pi-box',
    '.rar': 'pi pi-box',
    '.7z': 'pi pi-box',
    '.mp3': 'pi pi-volume-up',
    '.mp4': 'pi pi-video',
    '.mkv': 'pi pi-video',
  };
  return `${map[ext] ?? 'pi pi-file'} file-icon`;
}
