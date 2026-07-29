import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { DialogModule } from 'primeng/dialog';
import { MessageModule } from 'primeng/message';
import { SelectModule } from 'primeng/select';
import { TabsModule } from 'primeng/tabs';
import { FileEntry } from '../../../../core/models/file-system.model';
import { Permissions, PrincipalsResponse } from '../../../../core/models/permissions.model';
import { FilesService } from '../../../../core/services/files.service';
import { PermissionsService } from '../../../../core/services/permissions.service';
import { fileIconClass, formatBytes, formatDateTime } from '../../../../shared/format.util';
import { DirectorySizeTracker, FolderSizeState } from '../../../../shared/directory-size-tracker';

@Component({
  selector: 'app-properties-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, DialogModule, TabsModule, ButtonModule, CheckboxModule, SelectModule, MessageModule],
  templateUrl: './properties-dialog.component.html',
  styleUrl: './properties-dialog.component.scss',
})
export class PropertiesDialogComponent implements OnChanges, OnDestroy {
  @Input() visible = false;
  @Input() entries: FileEntry[] = [];
  @Output() readonly visibleChange = new EventEmitter<boolean>();

  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly permissions = signal<Permissions | null>(null);
  readonly principals = signal<PrincipalsResponse>({ users: [], groups: [] });
  readonly folderSizes = signal<ReadonlyMap<string, FolderSizeState>>(new Map());

  selectedOwner = '';
  selectedGroup = '';
  ownerRead = false;
  ownerWrite = false;
  ownerExecute = false;
  groupRead = false;
  groupWrite = false;
  groupExecute = false;
  otherRead = false;
  otherWrite = false;
  otherExecute = false;

  readonly formatBytes = formatBytes;
  readonly formatDateTime = formatDateTime;
  readonly fileIconClass = fileIconClass;

  private sizeTracker: DirectorySizeTracker;

  constructor(
    private readonly permissionsService: PermissionsService,
    private readonly filesService: FilesService,
  ) {
    this.sizeTracker = this.createSizeTracker();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['visible']?.currentValue === true && this.entries.length > 0) {
      void this.load();
      this.trackFolderSizes();
    } else if (changes['visible']?.currentValue === false) {
      this.resetSizeTracker();
    }
  }

  ngOnDestroy(): void {
    this.sizeTracker.dispose();
  }

  get isMultiple(): boolean {
    return this.entries.length > 1;
  }

  get primaryEntry(): FileEntry | undefined {
    return this.entries[0];
  }

  get totalSize(): number {
    return this.entries.reduce((sum, entry) => {
      if (entry.isDirectory) {
        return sum + (this.folderSizes().get(entry.path)?.bytes ?? 0);
      }
      return sum + (entry.sizeBytes ?? 0);
    }, 0);
  }

  get isCalculatingSize(): boolean {
    return this.entries.some((entry) => entry.isDirectory && (this.folderSizes().get(entry.path)?.status ?? 'Running') === 'Running');
  }

  get commonTypeLabel(): string {
    if (this.entries.length === 0) {
      return '';
    }
    const types = new Set(this.entries.map((entry) => entry.typeLabel));
    return types.size === 1 ? this.entries[0].typeLabel : 'Multiple types';
  }

  get computedOctal(): string {
    const toDigit = (r: boolean, w: boolean, x: boolean) => (r ? 4 : 0) + (w ? 2 : 0) + (x ? 1 : 0);
    return [
      toDigit(this.ownerRead, this.ownerWrite, this.ownerExecute),
      toDigit(this.groupRead, this.groupWrite, this.groupExecute),
      toDigit(this.otherRead, this.otherWrite, this.otherExecute),
    ].join('');
  }

  private createSizeTracker(): DirectorySizeTracker {
    return new DirectorySizeTracker(this.filesService, (state) => this.folderSizes.set(new Map(state)));
  }

  private resetSizeTracker(): void {
    this.sizeTracker.dispose();
    this.sizeTracker = this.createSizeTracker();
    this.folderSizes.set(new Map());
  }

  private trackFolderSizes(): void {
    for (const entry of this.entries) {
      if (entry.isDirectory) {
        this.sizeTracker.track(entry.path);
      }
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set(null);
    try {
      const [permissions, principals] = await Promise.all([
        this.permissionsService.get(this.primaryEntry!.path),
        this.permissionsService.principals(),
      ]);
      this.permissions.set(permissions);
      this.principals.set(principals);
      this.selectedOwner = permissions.owner;
      this.selectedGroup = permissions.group;
      this.applyOctal(permissions.octalMode || '000');
    } catch {
      this.errorMessage.set('Could not load permissions for this platform.');
    } finally {
      this.loading.set(false);
    }
  }

  private applyOctal(octal: string): void {
    const digits = octal.padStart(3, '0').slice(-3).split('').map(Number);
    [this.ownerRead, this.ownerWrite, this.ownerExecute] = this.bitsFor(digits[0]);
    [this.groupRead, this.groupWrite, this.groupExecute] = this.bitsFor(digits[1]);
    [this.otherRead, this.otherWrite, this.otherExecute] = this.bitsFor(digits[2]);
  }

  private bitsFor(digit: number): [boolean, boolean, boolean] {
    return [(digit & 4) !== 0, (digit & 2) !== 0, (digit & 1) !== 0];
  }

  async save(): Promise<void> {
    this.saving.set(true);
    this.errorMessage.set(null);
    try {
      await Promise.all(
        this.entries.map((entry) =>
          this.permissionsService.update(entry.path, this.computedOctal, this.selectedOwner, this.selectedGroup),
        ),
      );
      this.close();
    } catch (error) {
      this.errorMessage.set(this.extractError(error));
    } finally {
      this.saving.set(false);
    }
  }

  close(): void {
    this.visibleChange.emit(false);
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
}
