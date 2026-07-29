import { Injectable } from '@angular/core';
import { apiClient } from '../http/api-client';
import { FileEntry } from '../models/file-system.model';

@Injectable({ providedIn: 'root' })
export class SearchService {
  async search(path: string, query: string): Promise<FileEntry[]> {
    const { data } = await apiClient.get<FileEntry[]>('/search', { params: { path, query } });
    return data;
  }
}
