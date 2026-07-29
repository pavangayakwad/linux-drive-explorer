import { Injectable } from '@angular/core';
import { apiClient } from '../http/api-client';
import { TrashItem } from '../models/job.model';

@Injectable({ providedIn: 'root' })
export class TrashService {
  async list(): Promise<TrashItem[]> {
    const { data } = await apiClient.get<TrashItem[]>('/trash');
    return data;
  }

  async restore(id: string): Promise<void> {
    await apiClient.post(`/trash/${id}/restore`);
  }

  /** Checks whether there's enough free space to move all the given items to the Trash at once. */
  async checkSpace(sources: string[]): Promise<boolean> {
    const { data } = await apiClient.post<{ hasSpace: boolean }>('/trash/check-space', { sources });
    return data.hasSpace;
  }

  /** Permanently deletes the given items (or everything in the trash if ids is omitted). Returns the job id. */
  async purge(ids?: string[]): Promise<string> {
    const { data } = await apiClient.post<{ jobId: string }>('/trash/purge', { ids: ids ?? null });
    return data.jobId;
  }
}
