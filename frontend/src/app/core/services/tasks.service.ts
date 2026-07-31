import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { apiClient } from '../http/api-client';
import { tokenStore } from '../http/token-store';
import { Job } from '../models/job.model';
import { downloadBlob } from '../../shared/download.util';

@Injectable({ providedIn: 'root' })
export class TasksService {
  private connection: signalR.HubConnection | null = null;

  async list(): Promise<Job[]> {
    const { data } = await apiClient.get<Job[]>('/tasks');
    return data;
  }

  async cancel(jobId: string): Promise<void> {
    await apiClient.post(`/tasks/${jobId}/cancel`);
  }

  async downloadZip(jobId: string): Promise<void> {
    await downloadBlob(`/files/download-zip/${jobId}`, {}, 'Archive.zip');
  }

  async clearFinished(): Promise<void> {
    await apiClient.delete('/tasks/finished');
  }

  /** Connects to the live job-progress hub. Call disconnect() when the consuming view is destroyed. */
  async connect(onJobUpdated: (job: Job) => void): Promise<void> {
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/tasks', { accessTokenFactory: () => tokenStore.accessToken ?? '' })
      .withAutomaticReconnect()
      .build();

    this.connection.on('jobUpdated', onJobUpdated);
    await this.connection.start();
  }

  async disconnect(): Promise<void> {
    await this.connection?.stop();
    this.connection = null;
  }
}
