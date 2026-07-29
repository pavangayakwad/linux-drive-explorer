import { Injectable } from '@angular/core';
import { DriveSummary } from '../models/file-system.model';

const HISTORY_STORAGE_KEY = 'fx_visited_paths';
const MAX_HISTORY = 200;

/**
 * Remembers, per drive, the last folder visited and any active search query - so switching to
 * another drive and back doesn't dump the user back at the drive root with search cleared.
 *
 * Visited paths are kept as a flat recency-ordered history (persisted to localStorage) rather
 * than being classified into a mountPath at save time. Classifying eagerly would race the drive
 * list load: ExplorerComponent's first navigation can fire before ShellComponent's async drive
 * fetch resolves, silently dropping the save. Resolving the mountPath lazily at *restore* time
 * (see getPath) sidesteps this - by the time a user can click a drive button in the sidebar, the
 * drive list is guaranteed to be loaded.
 */
@Injectable({ providedIn: 'root' })
export class DriveNavigationStateService {
  private drives: DriveSummary[] = [];
  private readonly visitedPaths: string[] = this.loadHistory();
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

  /** Records that `path` was visited, most-recent-last. Safe to call even when the drive list
   * isn't loaded yet - which drive it belongs to is only worked out when getPath needs it. */
  recordVisit(path: string): void {
    const existingIndex = this.visitedPaths.indexOf(path);
    if (existingIndex !== -1) {
      this.visitedPaths.splice(existingIndex, 1);
    }
    this.visitedPaths.push(path);
    if (this.visitedPaths.length > MAX_HISTORY) {
      this.visitedPaths.shift();
    }
    this.persistHistory();
  }

  /** Returns the most recently visited path belonging to `mountPath`, if any. */
  getPath(mountPath: string): string | undefined {
    for (let i = this.visitedPaths.length - 1; i >= 0; i--) {
      const path = this.visitedPaths[i];
      if (this.mountPathFor(path) === mountPath) {
        return path;
      }
    }
    return undefined;
  }

  private loadHistory(): string[] {
    try {
      const raw = localStorage.getItem(HISTORY_STORAGE_KEY);
      if (!raw) {
        return [];
      }
      const parsed: unknown = JSON.parse(raw);
      return Array.isArray(parsed) ? parsed.filter((p): p is string => typeof p === 'string') : [];
    } catch {
      return [];
    }
  }

  private persistHistory(): void {
    try {
      localStorage.setItem(HISTORY_STORAGE_KEY, JSON.stringify(this.visitedPaths));
    } catch {
      // Best-effort only - a full/unavailable localStorage shouldn't break navigation.
    }
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
