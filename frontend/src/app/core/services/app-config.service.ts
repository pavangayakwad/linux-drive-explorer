import { Injectable, signal } from '@angular/core';
import { apiClient } from '../http/api-client';

const BASE_TITLE = 'Drive Explorer';

@Injectable({ providedIn: 'root' })
export class AppConfigService {
  readonly appName = signal('');

  // Runs at startup; on any failure no custom name is shown.
  async load(): Promise<void> {
    try {
      const { data } = await apiClient.get<{ appName?: string }>('/config');
      if (data.appName?.trim()) {
        this.appName.set(data.appName.trim());
      }
    } catch {
      // keep default
    }
    document.title = this.appName() ? `${this.appName()} - ${BASE_TITLE}` : BASE_TITLE;
  }
}
