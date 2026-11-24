import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges, signal } from '@angular/core';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { MessageModule } from 'primeng/message';
import { apiClient } from '../../../../core/http/api-client';
import { FileEntry } from '../../../../core/models/file-system.model';
import { formatBytes } from '../../../../shared/format.util';

type PreviewKind = 'image' | 'text' | 'pdf' | 'audio' | 'video' | 'unsupported';

const IMAGE_EXTENSIONS = new Set(['.png', '.jpg', '.jpeg', '.gif', '.svg', '.webp', '.bmp', '.ico', '.tif', '.tiff', '.avif']);
const AUDIO_EXTENSIONS = new Set(['.mp3', '.wav', '.ogg', '.oga', '.flac', '.m4a', '.aac', '.opus']);
const VIDEO_EXTENSIONS = new Set(['.mp4', '.webm', '.mov', '.mkv', '.avi', '.m4v', '.ogv']);

/**
 * Whether the server has said this file is text is a question about its bytes, not about its name -
 * "caddy", "Dockerfile", "authorized_keys" and ".env.example" carry no useful extension at all, while a
 * ".txt" that happens to hold a compiled binary is not text either. So the response's content type is the
 * authority, and the extension is only a fallback for the rare payload served without a usable one.
 */
function kindForContentType(contentType: string): PreviewKind | null {
  if (contentType.startsWith('text/')) return 'text';
  if (contentType === 'application/pdf') return 'pdf';
  if (contentType.startsWith('image/')) return 'image';
  if (contentType.startsWith('audio/')) return 'audio';
  if (contentType.startsWith('video/')) return 'video';
  return null;
}

function kindForExtension(extension: string): PreviewKind {
  if (extension === '.pdf') return 'pdf';
  if (IMAGE_EXTENSIONS.has(extension)) return 'image';
  if (AUDIO_EXTENSIONS.has(extension)) return 'audio';
  if (VIDEO_EXTENSIONS.has(extension)) return 'video';
  return 'unsupported';
}

@Component({
  selector: 'app-preview-dialog',
  standalone: true,
  imports: [CommonModule, ButtonModule, DialogModule, MessageModule],
  templateUrl: './preview-dialog.component.html',
  styleUrl: './preview-dialog.component.scss',
})
export class PreviewDialogComponent implements OnChanges, OnDestroy {
  @Input() visible = false;
  @Input() entry: FileEntry | null = null;
  @Output() readonly visibleChange = new EventEmitter<boolean>();

  readonly loading = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly kind = signal<PreviewKind>('unsupported');
  readonly objectUrl = signal<SafeResourceUrl | null>(null);
  readonly textContent = signal<string>('');
  /** True when the server sent only the head of a larger text file (see FilesController.Preview). */
  readonly truncated = signal(false);
  readonly totalBytes = signal<number | null>(null);

  readonly formatBytes = formatBytes;

  readonly editing = signal(false);
  readonly saving = signal(false);
  readonly draft = signal('');
  readonly saveError = signal<string | null>(null);

  private rawObjectUrl: string | null = null;

  startEdit(): void {
    this.draft.set(this.textContent());
    this.saveError.set(null);
    this.editing.set(true);
  }

  cancelEdit(): void {
    this.editing.set(false);
    this.saveError.set(null);
  }

  onDraftInput(event: Event): void {
    this.draft.set((event.target as HTMLTextAreaElement).value);
  }

  async save(): Promise<void> {
    const entry = this.entry;
    if (!entry || this.saving()) {
      return;
    }
    this.saving.set(true);
    this.saveError.set(null);
    try {
      await apiClient.put('/files/content', { path: entry.path, content: this.draft() });
      this.textContent.set(this.draft());
      this.editing.set(false);
    } catch (error) {
      const message = (error as { response?: { data?: { message?: string } } })?.response?.data?.message;
      this.saveError.set(message ?? 'Could not save the file.');
    } finally {
      this.saving.set(false);
    }
  }

  onTabKey(event: Event): void {
    const e = event as KeyboardEvent;
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
      e.preventDefault();
      void this.save();
    }
  }

  constructor(private readonly sanitizer: DomSanitizer) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['visible']?.currentValue === true && this.entry) {
      void this.load();
    }
    if (changes['visible']?.currentValue === false) {
      this.cleanup();
    }
  }

  ngOnDestroy(): void {
    this.cleanup();
  }

  private async load(): Promise<void> {
    this.cleanup();
    const entry = this.entry;
    if (!entry) {
      return;
    }

    this.loading.set(true);
    this.errorMessage.set(null);
    try {
      const response = await apiClient.get<Blob>('/files/preview', {
        params: { path: entry.path },
        responseType: 'blob',
      });

      const servedType = String(response.headers['content-type'] ?? '').split(';')[0].trim().toLowerCase();
      const kind = kindForContentType(servedType) ?? kindForExtension((entry.extension ?? '').toLowerCase());
      this.kind.set(kind);

      if (kind === 'text') {
        this.textContent.set(await response.data.text());
        this.truncated.set(String(response.headers['x-preview-truncated'] ?? 'false') === 'true');
        const total = Number(response.headers['x-preview-total-bytes']);
        this.totalBytes.set(Number.isFinite(total) && total > 0 ? total : null);
      } else if (kind !== 'unsupported') {
        this.rawObjectUrl = URL.createObjectURL(response.data);
        this.objectUrl.set(this.sanitizer.bypassSecurityTrustResourceUrl(this.rawObjectUrl));
      }
    } catch (error) {
      // 415 is the server having read the file and found compiled code, an archive, or media it won't
      // render inline - that is an ordinary "no preview" answer rather than a failure to load.
      if ((error as { response?: { status?: number } })?.response?.status === 415) {
        this.kind.set('unsupported');
      } else {
        this.errorMessage.set('Could not load a preview for this file.');
      }
    } finally {
      this.loading.set(false);
    }
  }

  private cleanup(): void {
    if (this.rawObjectUrl) {
      URL.revokeObjectURL(this.rawObjectUrl);
      this.rawObjectUrl = null;
    }
    this.objectUrl.set(null);
    this.textContent.set('');
    this.truncated.set(false);
    this.totalBytes.set(null);
    this.errorMessage.set(null);
    this.kind.set('unsupported');
    this.editing.set(false);
    this.saving.set(false);
    this.saveError.set(null);
    this.draft.set('');
  }

  close(): void {
    this.visibleChange.emit(false);
  }
}
