import { CommonModule } from '@angular/common';
import { Component, computed, OnDestroy, OnInit, signal } from '@angular/core';
import { ConfirmationService, MessageService } from 'primeng/api';
import { ButtonModule } from 'primeng/button';
import { ProgressBarModule } from 'primeng/progressbar';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { Job } from '../../core/models/job.model';
import { TasksService } from '../../core/services/tasks.service';
import { formatBytes, formatDateTime } from '../../shared/format.util';

const FINISHED_STATUSES: Job['status'][] = ['Completed', 'Failed', 'Cancelled'];

@Component({
  selector: 'app-tasks',
  standalone: true,
  imports: [CommonModule, TableModule, ButtonModule, ProgressBarModule, TagModule, TooltipModule],
  templateUrl: './tasks.component.html',
  styleUrl: './tasks.component.scss',
})
export class TasksComponent implements OnInit, OnDestroy {
  readonly jobs = signal<Job[]>([]);
  readonly loading = signal(false);

  readonly hasFinishedJobs = computed(() => this.jobs().some((job) => FINISHED_STATUSES.includes(job.status)));

  readonly formatBytes = formatBytes;
  readonly formatDateTime = formatDateTime;

  constructor(
    private readonly tasksService: TasksService,
    private readonly confirmationService: ConfirmationService,
    private readonly messageService: MessageService,
  ) {}

  async ngOnInit(): Promise<void> {
    this.loading.set(true);
    try {
      this.jobs.set(await this.tasksService.list());
    } finally {
      this.loading.set(false);
    }

    await this.tasksService.connect((job) => this.applyUpdate(job));
  }

  async ngOnDestroy(): Promise<void> {
    await this.tasksService.disconnect();
  }

  private applyUpdate(job: Job): void {
    const current = this.jobs();
    const index = current.findIndex((j) => j.id === job.id);
    if (index === -1) {
      this.jobs.set([job, ...current]);
    } else {
      const next = [...current];
      next[index] = job;
      this.jobs.set(next);
    }
  }

  progressPercent(job: Job): number {
    if (job.status === 'Completed') {
      return 100;
    }
    if (job.totalBytes > 0) {
      return Math.round((job.processedBytes / job.totalBytes) * 100);
    }
    if (job.totalItems > 0) {
      return Math.round((job.processedItems / job.totalItems) * 100);
    }
    return 0;
  }

  summary(job: Job): string {
    const paths = job.sources;
    const label = paths.length <= 2 ? paths.join(', ') : `${paths[0]} and ${paths.length - 1} more`;
    return job.destination ? `${label} → ${job.destination}` : label;
  }

  statusSeverity(status: Job['status']): 'info' | 'success' | 'danger' | 'warn' | 'secondary' {
    switch (status) {
      case 'Completed':
        return 'success';
      case 'Failed':
        return 'danger';
      case 'Cancelled':
        return 'secondary';
      case 'Running':
        return 'info';
      default:
        return 'warn';
    }
  }

  canCancel(job: Job): boolean {
    return job.status === 'Queued' || job.status === 'Running';
  }

  async cancel(job: Job): Promise<void> {
    await this.tasksService.cancel(job.id);
  }

  canDownload(job: Job): boolean {
    return job.type === 'Zip' && job.status === 'Completed';
  }

  async download(job: Job): Promise<void> {
    try {
      await this.tasksService.downloadZip(job.id);
    } catch {
      this.messageService.add({ severity: 'error', summary: 'Failed', detail: 'This archive is no longer available.' });
    }
  }

  confirmClearFinished(): void {
    this.confirmationService.confirm({
      header: 'Clear finished tasks',
      message: 'Remove all completed, failed, and cancelled tasks from this list?',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonProps: { severity: 'danger', label: 'Clear all' },
      accept: () => void this.clearFinished(),
    });
  }

  private async clearFinished(): Promise<void> {
    try {
      await this.tasksService.clearFinished();
      this.jobs.set(this.jobs().filter((job) => !FINISHED_STATUSES.includes(job.status)));
      this.messageService.add({ severity: 'success', summary: 'Cleared', detail: 'Finished tasks were removed.' });
    } catch {
      this.messageService.add({ severity: 'error', summary: 'Failed', detail: 'Could not clear finished tasks.' });
    }
  }
}
