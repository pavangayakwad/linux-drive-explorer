import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges, signal } from '@angular/core';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { DialogModule } from 'primeng/dialog';
import { MessageModule } from 'primeng/message';
import { apiClient } from '../../../../core/http/api-client';
import { FileEntry } from '../../../../core/models/file-system.model';

type PreviewKind = 'image' | 'text' | 'pdf' | 'audio' | 'video' | 'unsupported';

const TEXT_EXTENSIONS = new Set([
  '.txt', '.md', '.log', '.json', '.xml', '.csv', '.yml', '.yaml', '.ts', '.js',
  '.css', '.html', '.sh', '.cs', '.cshtml', '.sql', '.ini', '.conf', '.gitignore',
]);
const IMAGE_EXTENSIONS = new Set(['.png', '.jpg', '.jpeg', '.gif', '.svg', '.webp', '.bmp']);
const AUDIO_EXTENSIONS = new Set(['.mp3', '.wav', '.ogg']);
const VIDEO_EXTENSIONS = new Set(['.mp4', '.webm', '.mov']);

@Component({
  selector: 'app-preview-dialog',
  standalone: true,
  imports: [CommonModule, DialogModule, MessageModule],
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

  private rawObjectUrl: string | null = null;

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

    const extension = (entry.extension ?? '').toLowerCase();
    const kind = this.resolveKind(extension);
    this.kind.set(kind);

    if (kind === 'unsupported') {
      return;
    }

    this.loading.set(true);
    this.errorMessage.set(null);
    try {
      const { data } = await apiClient.get<Blob>('/files/preview', {
        params: { path: entry.path },
        responseType: 'blob',
      });

      if (kind === 'text') {
        this.textContent.set(await data.text());
      } else {
        this.rawObjectUrl = URL.createObjectURL(data);
        this.objectUrl.set(this.sanitizer.bypassSecurityTrustResourceUrl(this.rawObjectUrl));
      }
    } catch {
      this.errorMessage.set('Could not load a preview for this file.');
    } finally {
      this.loading.set(false);
    }
  }

  private resolveKind(extension: string): PreviewKind {
    if (TEXT_EXTENSIONS.has(extension)) return 'text';
    if (IMAGE_EXTENSIONS.has(extension)) return 'image';
    if (extension === '.pdf') return 'pdf';
    if (AUDIO_EXTENSIONS.has(extension)) return 'audio';
    if (VIDEO_EXTENSIONS.has(extension)) return 'video';
    return 'unsupported';
  }

  private cleanup(): void {
    if (this.rawObjectUrl) {
      URL.revokeObjectURL(this.rawObjectUrl);
      this.rawObjectUrl = null;
    }
    this.objectUrl.set(null);
    this.textContent.set('');
    this.errorMessage.set(null);
  }

  close(): void {
    this.visibleChange.emit(false);
  }
}
