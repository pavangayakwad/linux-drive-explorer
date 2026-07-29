import { Injectable } from '@angular/core';
import { DriveSummary } from '../models/file-system.model';

/**
 * Remembers, per drive, the last folder visited and any active search query - so switching to
 * another drive and back doesn't dump the user back at the drive root with search cleared.
 * Keyed by mountPath rather than the exact DriveSummary object since the drive list is
 * re-fetched (and its objects recreated) on every refresh/poll.
 */
@Injectable({ providedIn: 'root' })
export class DriveNavigationStateService {
  private drives: DriveSummary[] = [];
  private readonly lastPathByMount = new Map<string, string>();
  private readonly searchQueryByMount = new Map<string, string>();

  setDrives(drives: DriveSummary[]): void {
    this.drives = drives;
  }

  /** Resolves the drive whose mountPath is the longest matching prefix of `path`, so nested
   * mounts (e.g. "/" and "/mnt/c") both resolve to the more specific one. */
  mountPathFor(path: string): string | null {
    let best: string | null = null;
    for (const drive of this.drives) {
      const prefix = drive.mountPath.endsWith('/') ? drive.mountPath : `${drive.mountPath}/`;
      const isMatch = path === drive.mountPath || path.startsWith(prefix);
      if (isMatch && (best === null || drive.mountPath.length > best.length)) {
        best = drive.mountPath;
      }
    }
    return best;
  }

  savePath(mountPath: string, path: string): void {
    this.lastPathByMount.set(mountPath, path);
  }

  getPath(mountPath: string): string | undefined {
    return this.lastPathByMount.get(mountPath);
  }

  saveSearch(mountPath: string, query: string): void {
    this.searchQueryByMount.set(mountPath, query);
  }

  getSearch(mountPath: string): string | undefined {
    return this.searchQueryByMount.get(mountPath);
  }

  clearSearch(mountPath: string): void {
    this.searchQueryByMount.delete(mountPath);
  }
}
