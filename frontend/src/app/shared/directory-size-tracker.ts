import { FilesService } from '../core/services/files.service';
import { DirectorySizeJob, DirectorySizeStatus } from '../core/models/file-system.model';

export interface FolderSizeState {
  jobId: string;
  status: DirectorySizeStatus;
  bytes: number;
}

const POLL_INTERVAL_MS = 800;

/** Starts and polls background `du`-based size jobs for one or more folder paths, notifying a callback as results
 * come in. Owned by a single component instance - call dispose() when that instance no longer needs live updates
 * (dialog closed, toggled off, navigated away) so in-flight jobs are cancelled and polling stops. */
export class DirectorySizeTracker {
  private readonly state = new Map<string, FolderSizeState>();
  private readonly timers = new Map<string, ReturnType<typeof setInterval>>();

  constructor(
    private readonly filesService: FilesService,
    private readonly onUpdate: (state: ReadonlyMap<string, FolderSizeState>) => void,
  ) {}

  get(path: string): FolderSizeState | undefined {
    return this.state.get(path);
  }

  /** Begins tracking a folder's size if it isn't already being tracked. */
  track(path: string): void {
    if (this.state.has(path)) {
      return;
    }
    void this.start(path);
  }

  dispose(): void {
    for (const state of this.state.values()) {
      if (state.status === 'Running') {
        void this.filesService.cancelSizeJob(state.jobId).catch(() => {});
      }
    }
    for (const handle of this.timers.values()) {
      clearInterval(handle);
    }
    this.timers.clear();
    this.state.clear();
  }

  private async start(path: string): Promise<void> {
    try {
      const job = await this.filesService.startSizeJob(path);
      this.apply(path, job);
      if (job.status === 'Running') {
        this.poll(path, job.jobId);
      }
    } catch {
      // Leave the folder's size blank rather than surfacing an error for a non-essential feature.
    }
  }

  private poll(path: string, jobId: string): void {
    const handle = setInterval(async () => {
      try {
        const job = await this.filesService.getSizeJob(jobId);
        this.apply(path, job);
        if (job.status !== 'Running') {
          this.clearTimer(path);
        }
      } catch {
        this.clearTimer(path);
      }
    }, POLL_INTERVAL_MS);
    this.timers.set(path, handle);
  }

  private apply(path: string, job: DirectorySizeJob): void {
    this.state.set(path, { jobId: job.jobId, status: job.status, bytes: job.bytes });
    this.onUpdate(this.state);
  }

  private clearTimer(path: string): void {
    const handle = this.timers.get(path);
    if (handle !== undefined) {
      clearInterval(handle);
      this.timers.delete(path);
    }
  }
}
