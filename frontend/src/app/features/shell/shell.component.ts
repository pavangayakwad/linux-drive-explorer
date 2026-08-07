import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, NavigationEnd, Router, RouterOutlet } from '@angular/router';
import * as signalR from '@microsoft/signalr';
import { MessageService } from 'primeng/api';
import { filter, map, startWith } from 'rxjs';
import { DrivesService } from '../../core/services/drives.service';
import { DriveNavigationStateService } from '../../core/services/drive-navigation-state.service';
import { DriveSummary, UnmountedDevice } from '../../core/models/file-system.model';
import { Job } from '../../core/models/job.model';
import { tokenStore } from '../../core/http/token-store';
import { TasksService } from '../../core/services/tasks.service';
import { SidebarComponent } from '../../shared/components/sidebar/sidebar.component';
import { UploadProgressPanelComponent } from '../../shared/components/upload-progress-panel/upload-progress-panel.component';

const ACTIVE_JOB_STATUSES: ReadonlySet<Job['status']> = new Set(['Queued', 'Running']);

const MIN_WIDTH = 180;
const MAX_WIDTH = 480;
const DEFAULT_WIDTH = 248;
const STORAGE_KEY = 'fx_sidebar_width';

/** Drive free-space figures drift while a copy/move/delete job runs, so only worth
 * refreshing once a job reaches a terminal state. */
const TERMINAL_JOB_STATUSES: ReadonlySet<Job['status']> = new Set(['Completed', 'Failed', 'Cancelled']);

const DRIVES_POLL_INTERVAL_MS = 60_000;

function sortDrivesByFreeSpace(drives: DriveSummary[]): DriveSummary[] {
  return [...drives].sort((a, b) => b.freeBytes - a.freeBytes);
}

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [CommonModule, RouterOutlet, SidebarComponent, UploadProgressPanelComponent],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
})
export class ShellComponent implements OnInit, OnDestroy {
  private readonly drivesService = inject(DrivesService);
  private readonly driveNavState = inject(DriveNavigationStateService);
  private readonly tasksService = inject(TasksService);
  private readonly messageService = inject(MessageService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  private jobHubConnection: signalR.HubConnection | null = null;
  private drivesPollHandle: ReturnType<typeof setInterval> | null = null;
  private readonly jobStatuses = new Map<string, Job['status']>();
  /** How many of a job's permanently-deleted names we've already warned about, keyed by job id -
   * jobUpdated fires repeatedly as a delete progresses, so this stops the same names re-alerting. */
  private readonly warnedPermanentDeleteCounts = new Map<string, number>();

  readonly drives = signal<DriveSummary[]>([]);
  readonly unmountedDevices = signal<UnmountedDevice[]>([]);
  readonly refreshingDrives = signal(false);
  readonly mountingDevice = signal<string | null>(null);
  readonly unmountingPath = signal<string | null>(null);
  readonly sidebarWidth = signal(this.loadStoredWidth());
  readonly runningTaskCount = signal(0);

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map(() => this.router.url),
      startWith(this.router.url),
    ),
    { initialValue: this.router.url },
  );

  private readonly rawCurrentPath = toSignal(
    this.route.queryParamMap.pipe(map((params) => params.get('path') ?? '/')),
    { initialValue: '/' },
  );

  readonly isExplorerRoute = computed(() => this.url().split('?')[0] === '/');
  readonly currentPath = computed(() => (this.isExplorerRoute() ? this.rawCurrentPath() : ''));

  private resizing = false;

  ngOnInit(): void {
    void this.loadDrives();
    void this.loadInitialTaskCount();
    void this.connectJobHub();
    this.drivesPollHandle = setInterval(() => void this.loadDrives(), DRIVES_POLL_INTERVAL_MS);
  }

  async ngOnDestroy(): Promise<void> {
    if (this.drivesPollHandle !== null) {
      clearInterval(this.drivesPollHandle);
    }
    await this.jobHubConnection?.stop();
  }

  async loadDrives(): Promise<void> {
    try {
      this.drives.set(sortDrivesByFreeSpace(await this.drivesService.list()));
    } catch {
      this.drives.set([]);
    }
    this.driveNavState.setDrives(this.drives());
  }

  // The only place unmounted-device discovery happens - deliberately not part of loadDrives()'s
  // periodic poll, so scanning /sys/block only ever runs when the user asks for it.
  async refreshDrives(): Promise<void> {
    this.refreshingDrives.set(true);
    try {
      await Promise.all([this.loadDrives(), this.loadUnmountedDevices()]);
    } finally {
      this.refreshingDrives.set(false);
    }
  }

  private async loadUnmountedDevices(): Promise<void> {
    try {
      this.unmountedDevices.set(await this.drivesService.listUnmounted());
    } catch {
      this.unmountedDevices.set([]);
    }
  }

  async mountDevice(device: string): Promise<void> {
    this.mountingDevice.set(device);
    try {
      await this.drivesService.mount(device);
      await this.refreshDrives();
    } catch (error) {
      this.messageService.add({ severity: 'error', summary: 'Mount failed', detail: this.extractError(error) });
    } finally {
      this.mountingDevice.set(null);
    }
  }

  async unmountDrive(mountPath: string): Promise<void> {
    this.unmountingPath.set(mountPath);
    try {
      await this.drivesService.unmount(mountPath);
      await this.refreshDrives();
    } catch (error) {
      this.messageService.add({ severity: 'error', summary: 'Unmount failed', detail: this.extractError(error) });
    } finally {
      this.unmountingPath.set(null);
    }
  }

  private extractError(error: unknown): string {
    if (error && typeof error === 'object' && 'response' in error) {
      const response = (error as { response?: { data?: { message?: string } } }).response;
      if (response?.data?.message) {
        return response.data.message;
      }
    }
    return 'Something went wrong.';
  }

  private async loadInitialTaskCount(): Promise<void> {
    try {
      const jobs = await this.tasksService.list();
      for (const job of jobs) {
        this.jobStatuses.set(job.id, job.status);
      }
      this.recomputeRunningTaskCount();
    } catch {
      // Best-effort - the count will still update live as job events arrive.
    }
  }

  /** Separate hub connection from the one Tasks/Trash pages open for themselves - this one
   * lives for as long as the shell does, purely to know when a copy/move/delete finishes so
   * the drive free-space figures can be refreshed, and to keep the sidebar's running-task
   * count up to date. */
  private async connectJobHub(): Promise<void> {
    this.jobHubConnection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/tasks', { accessTokenFactory: () => tokenStore.accessToken ?? '' })
      .withAutomaticReconnect()
      .build();

    this.jobHubConnection.on('jobUpdated', (job: Job) => {
      const previousStatus = this.jobStatuses.get(job.id);
      this.jobStatuses.set(job.id, job.status);
      this.recomputeRunningTaskCount();
      this.warnAboutPermanentDeletes(job);
      if (TERMINAL_JOB_STATUSES.has(job.status)) {
        void this.loadDrives();
      }
      if (job.type === 'Zip' && job.status === 'Completed' && previousStatus !== 'Completed') {
        void this.autoDownloadZip(job);
      }
    });

    try {
      await this.jobHubConnection.start();
    } catch {
      // Best-effort - the periodic poll still keeps drive sizes reasonably fresh.
    }
  }

  /** Shows a prominent warning whenever a delete job falls back to permanently deleting
   * an item because its drive had no room left to hold a trashed copy. */
  private warnAboutPermanentDeletes(job: Job): void {
    const names = job.permanentlyDeletedNames;
    const alreadyWarned = this.warnedPermanentDeleteCounts.get(job.id) ?? 0;
    if (names.length <= alreadyWarned) {
      return;
    }
    const newNames = names.slice(alreadyWarned);
    this.warnedPermanentDeleteCounts.set(job.id, names.length);

    this.messageService.add({
      severity: 'warn',
      summary: 'Permanently deleted - not moved to Trash',
      detail: `Not enough free disk space to keep ${newNames.length === 1 ? `"${newNames[0]}"` : `${newNames.length} items`} in Trash, so ${newNames.length === 1 ? 'it was' : 'they were'} deleted permanently. This cannot be undone.`,
    });
  }

  // Fires the browser download the moment a zip finishes, wherever the user currently is in the
  // app, instead of making them go find the completed job in the Tasks list and click Download
  // themselves. The server deletes the staged archive once this download's response completes
  // (see FilesController.DownloadZip), so this is also the archive's only real chance to be fetched.
  private async autoDownloadZip(job: Job): Promise<void> {
    try {
      await this.tasksService.downloadZip(job.id);
    } catch {
      this.messageService.add({
        severity: 'error',
        summary: 'Download failed',
        detail: 'The archive could not be downloaded automatically. Open Tasks to try again.',
      });
    }
  }

  private recomputeRunningTaskCount(): void {
    let count = 0;
    for (const status of this.jobStatuses.values()) {
      if (ACTIVE_JOB_STATUSES.has(status)) {
        count++;
      }
    }
    this.runningTaskCount.set(count);
  }

  onNavigate(path: string): void {
    void this.router.navigate(['/'], { queryParams: { path } });
  }

  startResize(event: MouseEvent): void {
    event.preventDefault();
    this.resizing = true;

    const onMove = (moveEvent: MouseEvent) => {
      if (!this.resizing) {
        return;
      }
      const width = Math.min(MAX_WIDTH, Math.max(MIN_WIDTH, moveEvent.clientX));
      this.sidebarWidth.set(width);
    };

    const onUp = () => {
      this.resizing = false;
      localStorage.setItem(STORAGE_KEY, String(this.sidebarWidth()));
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };

    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }

  private loadStoredWidth(): number {
    const stored = Number(localStorage.getItem(STORAGE_KEY));
    return stored >= MIN_WIDTH && stored <= MAX_WIDTH ? stored : DEFAULT_WIDTH;
  }
}
