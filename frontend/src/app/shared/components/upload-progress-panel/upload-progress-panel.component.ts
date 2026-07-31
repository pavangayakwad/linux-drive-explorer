import { CommonModule } from '@angular/common';
import { Component, computed } from '@angular/core';
import { ButtonModule } from 'primeng/button';
import { MessageService } from 'primeng/api';
import { TooltipModule } from 'primeng/tooltip';
import { UploadService } from '../../../core/services/upload.service';
import { UploadItem } from '../../../core/models/upload.model';
import { fileIconClass, formatBytes } from '../../format.util';

@Component({
  selector: 'app-upload-progress-panel',
  standalone: true,
  imports: [CommonModule, ButtonModule, TooltipModule],
  templateUrl: './upload-progress-panel.component.html',
  styleUrl: './upload-progress-panel.component.scss',
})
export class UploadProgressPanelComponent {
  readonly fileIconClass = fileIconClass;
  readonly formatBytes = formatBytes;

  readonly doneCount = computed(() => this.uploadService.items().filter((i) => i.status === 'done').length);

  constructor(
    readonly uploadService: UploadService,
    private readonly messageService: MessageService,
  ) {}

  headerLabel(): string {
    if (this.uploadService.isActive()) {
      return `Uploading ${this.uploadService.totalCount()} item${this.uploadService.totalCount() === 1 ? '' : 's'}`;
    }
    const hasErrors = this.uploadService.items().some((i) => i.status === 'error');
    if (hasErrors) {
      return 'Upload finished with errors';
    }
    return `Uploaded ${this.doneCount()} item${this.doneCount() === 1 ? '' : 's'}`;
  }

  itemIcon(item: UploadItem): string {
    const extension = item.name.includes('.') ? item.name.slice(item.name.lastIndexOf('.')) : null;
    return fileIconClass(false, extension);
  }

  canCancel(item: UploadItem): boolean {
    return item.status === 'pending' || item.status === 'uploading';
  }

  itemPercent(item: UploadItem): number {
    if (item.totalBytes === 0) {
      return 0;
    }
    return Math.min(100, Math.round((item.loadedBytes / item.totalBytes) * 100));
  }

  cancelItem(item: UploadItem): void {
    this.uploadService.cancelItem(item.id);
  }

  showError(item: UploadItem): void {
    if (item.status !== 'error') {
      return;
    }
    this.messageService.add({
      severity: 'error',
      summary: `Failed to upload "${item.name}"`,
      detail: item.errorMessage ?? 'Upload failed.',
      sticky: true,
    });
  }

  cancelAll(): void {
    this.uploadService.cancelAll();
  }

  toggleCollapsed(): void {
    this.uploadService.toggleCollapsed();
  }

  close(): void {
    this.uploadService.dismiss();
  }
}
