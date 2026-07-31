import { Injectable, computed, signal } from '@angular/core';
import { AxiosProgressEvent } from 'axios';
import { apiClient } from '../http/api-client';
import { DroppedEntry } from '../../shared/dropped-entries.util';
import { UploadItem } from '../models/upload.model';
import { FilesService } from './files.service';

const CONCURRENCY = 5;
const MAX_NAME_CONFLICT_ATTEMPTS = 50;

interface InternalItem extends UploadItem {
  file: File;
}

/**
 * Drives every upload (single file, multi-file, whole folders, from a picker or an OS drag-and-drop) through one
 * concurrency-limited pool of HTTP requests. Root-provided so a batch keeps uploading - and the progress panel
 * keeps showing it - no matter which page the user navigates to while it runs.
 */
@Injectable({ providedIn: 'root' })
export class UploadService {
  private readonly itemsMap = new Map<string, InternalItem>();
  private readonly controllers = new Map<string, AbortController>();
  private nextId = 0;
  private batchStartedAt: number | null = null;

  readonly items = signal<UploadItem[]>([]);
  readonly visible = signal(false);
  readonly collapsed = signal(false);

  readonly totalCount = computed(() => this.items().length);
  readonly activeCount = computed(() => this.items().filter((i) => i.status === 'pending' || i.status === 'uploading').length);
  readonly isActive = computed(() => this.activeCount() > 0);
  readonly allDone = computed(() => this.totalCount() > 0 && this.activeCount() === 0);

  readonly overallPercent = computed(() => {
    const items = this.items();
    const totalBytes = items.reduce((sum, i) => sum + i.totalBytes, 0);
    if (totalBytes === 0) {
      return 0;
    }
    const loadedBytes = items.reduce((sum, i) => sum + i.loadedBytes, 0);
    return Math.min(100, Math.round((loadedBytes / totalBytes) * 100));
  });

  readonly remainingLabel = computed(() => {
    if (!this.isActive() || this.batchStartedAt === null) {
      return '';
    }
    const items = this.items();
    const totalBytes = items.reduce((sum, i) => sum + i.totalBytes, 0);
    const loadedBytes = items.reduce((sum, i) => sum + i.loadedBytes, 0);
    const remainingBytes = totalBytes - loadedBytes;
    const elapsedSeconds = (Date.now() - this.batchStartedAt) / 1000;
    if (remainingBytes <= 0 || elapsedSeconds < 1) {
      return 'Almost done...';
    }
    const bytesPerSecond = loadedBytes / elapsedSeconds;
    if (bytesPerSecond <= 0) {
      return 'Calculating time left...';
    }
    const remainingSeconds = remainingBytes / bytesPerSecond;
    return formatRemaining(remainingSeconds);
  });

  constructor(private readonly filesService: FilesService) {}

  /** Resolves once every item in this batch has settled (done/error/cancelled). */
  async startUpload(entries: DroppedEntry[], destinationPath: string): Promise<void> {
    if (entries.length === 0) {
      return;
    }

    const resolvedEntries = await this.resolveFolderNameConflicts(entries, destinationPath);

    const batch: InternalItem[] = resolvedEntries.map((entry) => ({
      id: `up-${Date.now()}-${this.nextId++}`,
      name: entry.file.name,
      relativePath: entry.relativePath,
      destinationPath,
      status: 'pending',
      loadedBytes: 0,
      totalBytes: entry.file.size,
      file: entry.file,
    }));

    for (const item of batch) {
      this.itemsMap.set(item.id, item);
    }
    this.batchStartedAt ??= Date.now();
    this.publish();
    this.visible.set(true);
    this.collapsed.set(false);

    await this.runPool(batch);
  }

  cancelItem(id: string): void {
    const item = this.itemsMap.get(id);
    if (!item || item.status === 'done' || item.status === 'error' || item.status === 'cancelled') {
      return;
    }
    this.controllers.get(id)?.abort();
    this.updateItem(id, { status: 'cancelled' });
  }

  cancelAll(): void {
    for (const item of this.itemsMap.values()) {
      if (item.status === 'pending' || item.status === 'uploading') {
        this.controllers.get(item.id)?.abort();
        this.updateItem(item.id, { status: 'cancelled' });
      }
    }
  }

  dismiss(): void {
    this.visible.set(false);
    this.itemsMap.clear();
    this.controllers.clear();
    this.batchStartedAt = null;
    this.publish();
  }

  toggleCollapsed(): void {
    this.collapsed.update((collapsed) => !collapsed);
  }

  private async runPool(batch: InternalItem[]): Promise<void> {
    let cursor = 0;
    const workerCount = Math.min(CONCURRENCY, batch.length);
    await Promise.all(
      Array.from({ length: workerCount }, async () => {
        while (cursor < batch.length) {
          const item = batch[cursor++];
          if (item.status === 'cancelled') {
            continue;
          }
          await this.uploadOne(item);
        }
      }),
    );
  }

  private async uploadOne(item: InternalItem): Promise<void> {
    const controller = new AbortController();
    this.controllers.set(item.id, controller);
    this.updateItem(item.id, { status: 'uploading' });

    const formData = new FormData();
    formData.append('destinationPath', item.destinationPath);
    formData.append('relativePath', item.relativePath);
    formData.append('file', item.file);

    try {
      await apiClient.post('/files/upload', formData, {
        signal: controller.signal,
        onUploadProgress: (event: AxiosProgressEvent) => {
          this.updateItem(item.id, { loadedBytes: event.loaded, totalBytes: event.total ?? item.totalBytes });
        },
      });
      this.updateItem(item.id, { status: 'done', loadedBytes: item.totalBytes });
    } catch (error) {
      if (this.itemsMap.get(item.id)?.status === 'cancelled') {
        return;
      }
      this.updateItem(item.id, { status: 'error', errorMessage: this.extractError(error) });
    } finally {
      this.controllers.delete(item.id);
    }
  }

  /** Resolves each unique top-level folder name in this batch to a collision-free name once (reusing the normal
   * create-folder endpoint), then rewrites every nested entry's relativePath to use it - this way concurrent
   * per-file uploads never race to rename the same new folder differently. */
  private async resolveFolderNameConflicts(entries: DroppedEntry[], destinationPath: string): Promise<DroppedEntry[]> {
    const topNames = new Set<string>();
    for (const entry of entries) {
      const slashIndex = entry.relativePath.indexOf('/');
      if (slashIndex > 0) {
        topNames.add(entry.relativePath.slice(0, slashIndex));
      }
    }
    if (topNames.size === 0) {
      return entries;
    }

    const resolved = new Map<string, string>();
    for (const name of topNames) {
      resolved.set(name, await this.resolveFolderName(destinationPath, name));
    }

    return entries.map((entry) => {
      const slashIndex = entry.relativePath.indexOf('/');
      if (slashIndex <= 0) {
        return entry;
      }
      const top = entry.relativePath.slice(0, slashIndex);
      const rest = entry.relativePath.slice(slashIndex);
      return { ...entry, relativePath: `${resolved.get(top)}${rest}` };
    });
  }

  private async resolveFolderName(destinationPath: string, name: string): Promise<string> {
    let candidate = name;
    for (let attempt = 1; attempt <= MAX_NAME_CONFLICT_ATTEMPTS; attempt++) {
      try {
        await this.filesService.createEntry(destinationPath, candidate, true);
        return candidate;
      } catch (error) {
        if (!this.isConflict(error)) {
          throw error;
        }
        candidate = `${name} (${attempt + 1})`;
      }
    }
    return candidate;
  }

  private isConflict(error: unknown): boolean {
    return (
      !!error &&
      typeof error === 'object' &&
      'response' in error &&
      (error as { response?: { status?: number } }).response?.status === 409
    );
  }

  private extractError(error: unknown): string {
    if (error && typeof error === 'object' && 'response' in error) {
      const response = (error as { response?: { data?: { message?: string } } }).response;
      if (response?.data?.message) {
        return response.data.message;
      }
    }
    return 'Upload failed.';
  }

  private updateItem(id: string, patch: Partial<InternalItem>): void {
    const current = this.itemsMap.get(id);
    if (!current) {
      return;
    }
    this.itemsMap.set(id, { ...current, ...patch });
    this.publish();
  }

  private publish(): void {
    this.items.set(Array.from(this.itemsMap.values()));
  }
}

function formatRemaining(seconds: number): string {
  if (seconds < 60) {
    return 'Less than a minute left';
  }
  const minutes = Math.ceil(seconds / 60);
  if (minutes < 60) {
    return `${minutes} minute${minutes === 1 ? '' : 's'} left`;
  }
  const hours = Math.round(minutes / 60);
  return `${hours} hour${hours === 1 ? '' : 's'} left`;
}
