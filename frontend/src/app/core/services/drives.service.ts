import { Injectable } from '@angular/core';
import { apiClient } from '../http/api-client';
import { DriveSummary } from '../models/file-system.model';

@Injectable({ providedIn: 'root' })
export class DrivesService {
  async list(): Promise<DriveSummary[]> {
    const { data } = await apiClient.get<DriveSummary[]>('/drives');
    return data;
  }
}
