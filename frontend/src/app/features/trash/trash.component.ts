import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { ConfirmationService, MessageService } from 'primeng/api';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TooltipModule } from 'primeng/tooltip';
import { Job } from '../../core/models/job.model';
import { TrashItem } from '../../core/models/job.model';
import { TasksService } from '../../core/services/tasks.service';
import { TrashService } from '../../core/services/trash.service';
import { formatBytes, formatDateTime } from '../../shared/format.util';

@Component({
  selector: 'app-trash',
  standalone: true,
  imports: [CommonModule, TableModule, ButtonModule, TooltipModule],
  templateUrl: './trash.component.html',
  styleUrl: './trash.component.scss',
})
export class TrashComponent implements OnInit, OnDestroy {
  readonly items = signal<TrashItem[]>([]);
  readonly loading = signal(false);
  readonly selection = signal<TrashItem[]>([]);

  readonly formatBytes = formatBytes;
  readonly formatDateTime = formatDateTime;

  constructor(
    private readonly trashService: TrashService,
    private readonly tasksService: TasksService,
    private readonly confirmationService: ConfirmationService,
    private readonly messageService: MessageService,
  ) {}

  async ngOnInit(): Promise<void> {
    await this.refresh();
    await this.tasksService.connect((job) => this.onJobUpdated(job));
  }

  async ngOnDestroy(): Promise<void> {
    await this.tasksService.disconnect();
  }

  private onJobUpdated(job: Job): void {
    if (job.type === 'PurgeTrash' && job.status === 'Completed') {
      void this.refresh();
    }
  }

  async refresh(): Promise<void> {
    this.loading.set(true);
    try {
      this.items.set(await this.trashService.list());
      this.selection.set([]);
    } finally {
      this.loading.set(false);
    }
  }

  async restore(item: TrashItem): Promise<void> {
    try {
      await this.trashService.restore(item.id);
      this.items.set(this.items().filter((i) => i.id !== item.id));
      this.messageService.add({ severity: 'success', summary: 'Restored', detail: `"${item.name}" was restored.` });
    } catch {
      this.messageService.add({ severity: 'error', summary: 'Restore failed', detail: `Could not restore "${item.name}".` });
    }
  }

  confirmPurgeSelected(): void {
    const selected = this.selection();
    this.confirmationService.confirm({
      header: 'Delete permanently',
      message: `Permanently delete ${selected.length} selected item(s)? This cannot be undone.`,
      icon: 'pi pi-exclamation-triangle',
      acceptButtonProps: { severity: 'danger', label: 'Delete permanently' },
      accept: () => void this.purge(selected.map((i) => i.id)),
    });
  }

  confirmEmptyTrash(): void {
    this.confirmationService.confirm({
      header: 'Empty trash',
      message: 'Permanently delete everything in the trash? This cannot be undone.',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonProps: { severity: 'danger', label: 'Empty trash' },
      accept: () => void this.purge(undefined),
    });
  }

  private async purge(ids: string[] | undefined): Promise<void> {
    await this.trashService.purge(ids);
    this.messageService.add({ severity: 'info', summary: 'Purging', detail: 'Permanent deletion has started.' });
  }
}
