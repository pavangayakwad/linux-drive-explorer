import { Injectable } from '@angular/core';
import { apiClient } from '../http/api-client';
import { DirectoryListing, DirectorySizeJob, FileEntry } from '../models/file-system.model';

@Injectable({ providedIn: 'root' })
export class FilesService {
  async list(path: string): Promise<DirectoryListing> {
    const { data } = await apiClient.get<DirectoryListing>('/files/list', { params: { path } });
    return data;
  }

  async createEntry(parentPath: string, name: string, isDirectory: boolean): Promise<FileEntry> {
    const { data } = await apiClient.post<FileEntry>('/files/entries', { parentPath, name, isDirectory });
    return data;
  }

  async rename(path: string, newName: string): Promise<FileEntry> {
    const { data } = await apiClient.put<FileEntry>('/files/rename', { path, newName });
    return data;
  }

  async startSizeJob(path: string): Promise<DirectorySizeJob> {
    const { data } = await apiClient.post<DirectorySizeJob>('/files/size', null, { params: { path } });
    return data;
  }

  async getSizeJob(jobId: string): Promise<DirectorySizeJob> {
    const { data } = await apiClient.get<DirectorySizeJob>(`/files/size/${jobId}`);
    return data;
  }

  async cancelSizeJob(jobId: string): Promise<void> {
    await apiClient.post(`/files/size/${jobId}/cancel`);
  }
}
