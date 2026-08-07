import { Injectable } from '@angular/core';
import { apiClient } from '../http/api-client';
import { DriveSummary, UnmountedDevice } from '../models/file-system.model';

@Injectable({ providedIn: 'root' })
export class DrivesService {
  async list(): Promise<DriveSummary[]> {
    const { data } = await apiClient.get<DriveSummary[]>('/drives');
    return data;
  }

  async listUnmounted(): Promise<UnmountedDevice[]> {
    const { data } = await apiClient.get<UnmountedDevice[]>('/drives/unmounted');
    return data;
  }

  async mount(device: string): Promise<DriveSummary> {
    const { data } = await apiClient.post<DriveSummary>('/drives/mount', { device });
    return data;
  }

  async unmount(mountPath: string): Promise<void> {
    await apiClient.post('/drives/unmount', { mountPath });
  }
}
