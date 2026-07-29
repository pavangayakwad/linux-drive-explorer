import { Injectable } from '@angular/core';
import { apiClient } from '../http/api-client';
import { Permissions, PrincipalsResponse } from '../models/permissions.model';

@Injectable({ providedIn: 'root' })
export class PermissionsService {
  async get(path: string): Promise<Permissions> {
    const { data } = await apiClient.get<Permissions>('/permissions', { params: { path } });
    return data;
  }

  async update(path: string, octalMode?: string, owner?: string, group?: string): Promise<void> {
    await apiClient.put('/permissions', { path, octalMode, owner, group });
  }

  async principals(): Promise<PrincipalsResponse> {
    const { data } = await apiClient.get<PrincipalsResponse>('/permissions/principals');
    return data;
  }
}
