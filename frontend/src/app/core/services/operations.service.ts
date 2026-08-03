import { Injectable } from '@angular/core';
import { apiClient } from '../http/api-client';
import { FileOperationType } from '../models/job.model';

@Injectable({ providedIn: 'root' })
export class OperationsService {
  async create(type: FileOperationType, sources: string[], destination?: string, permanent?: boolean): Promise<string> {
    const { data } = await apiClient.post<{ jobId: string }>('/operations', { type, sources, destination, permanent });
    return data.jobId;
  }
}
