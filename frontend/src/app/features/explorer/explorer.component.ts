import { CommonModule } from '@angular/common';
import {
  ChangeDetectorRef,
  Component,
  ElementRef,
  OnDestroy,
  OnInit,
  ViewChild,
  afterRenderEffect,
  computed,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { ButtonModule } from 'primeng/button';
import { ConfirmationService, MenuItem, MessageService } from 'primeng/api';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ContextMenuModule } from 'primeng/contextmenu';
import { ContextMenu } from 'primeng/contextmenu';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { TableModule } from 'primeng/table';
import { TooltipModule } from 'primeng/tooltip';
import { FilesService } from '../../core/services/files.service';
import { OperationsService } from '../../core/services/operations.service';
import { SearchService } from '../../core/services/search.service';
import { DriveNavigationStateService } from '../../core/services/drive-navigation-state.service';
import { TrashService } from '../../core/services/trash.service';
import { UploadService } from '../../core/services/upload.service';
import { DirectoryListing, FileEntry } from '../../core/models/file-system.model';
import { fileIconClass, formatBytes, formatDateTime, pathSegments } from '../../shared/format.util';
import { downloadBlob, getInsufficientSpaceInfo } from '../../shared/download.util';
import { collectFilesFromDataTransfer, collectFilesFromFileList } from '../../shared/dropped-entries.util';
import { PropertiesDialogComponent } from './components/properties-dialog/properties-dialog.component';
import { PreviewDialogComponent } from './components/preview-dialog/preview-dialog.component';
import { DirectorySizeTracker, FolderSizeState } from '../../shared/directory-size-tracker';

type FileEntryRow = FileEntry;

interface ClipboardState {
  mode: 'copy' | 'cut';
  paths: string[];
}

interface PromptState {
  title: string;
  label: string;
  onConfirm: (value: string) => void | Promise<void>;
}

interface DataColumnWidths {
  name: number;
  size: number;
  type: number;
  createdAt: number;
  modifiedAt: number;
}

// Floor for the Name column so it's never squeezed away to nothing - see measureColumnWidths.
const MIN_NAME_COLUMN_WIDTH = 192;

interface SortPreference {
  field: string;
  order: number;
}

const SORT_STORAGE_KEY = 'fx_sort_preference';
const DEFAULT_SORT: SortPreference = { field: 'modifiedAt', order: -1 };

function loadSortPreference(): SortPreference {
  try {
    const raw = localStorage.getItem(SORT_STORAGE_KEY);
    if (!raw) {
      return DEFAULT_SORT;
    }
    const parsed = JSON.parse(raw) as Partial<SortPreference>;
    if (typeof parsed.field === 'string' && (parsed.order === 1 || parsed.order === -1)) {
      return { field: parsed.field, order: parsed.order };
    }
  } catch {
    // ignore malformed storage
  }
  return DEFAULT_SORT;
}

function saveSortPreference(preference: SortPreference): void {
  try {
    localStorage.setItem(SORT_STORAGE_KEY, JSON.stringify(preference));
  } catch {
    // Best-effort only - a full/unavailable localStorage shouldn't break sorting.
  }
}

@Component({
  selector: 'app-explorer',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    TableModule,
    ButtonModule,
    TooltipModule,
    ConfirmDialogModule,
    ContextMenuModule,
    DialogModule,
    InputTextModule,
    PropertiesDialogComponent,
    PreviewDialogComponent,
  ],
  templateUrl: './explorer.component.html',
  styleUrl: './explorer.component.scss',
})
export class ExplorerComponent implements OnInit, OnDestroy {
  @ViewChild('rowMenu') rowMenu!: ContextMenu;
  @ViewChild('searchInput') searchInputRef?: ElementRef<HTMLInputElement>;
  @ViewChild('fileInput') fileInputRef?: ElementRef<HTMLInputElement>;
  @ViewChild('folderInput') folderInputRef?: ElementRef<HTMLInputElement>;

  readonly listing = signal<DirectoryListing | null>(null);
  readonly loading = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly selection = signal<FileEntry[]>([]);
  readonly focusedPath = signal<string | null>(null);
  readonly clipboard = signal<ClipboardState | null>(null);
  readonly promptDialog = signal<PromptState | null>(null);
  // Paths staged by confirmDelete() so the delete dialog's "Delete Permanently" footer button
  // (which bypasses the ConfirmationService accept/reject flow) knows what to delete.
  readonly pendingDeletePaths = signal<string[]>([]);
  readonly propertiesVisible = signal(false);
  readonly propertiesEntries = signal<FileEntry[]>([]);
  readonly previewVisible = signal(false);
  readonly previewEntry = signal<FileEntry | null>(null);
  readonly contextMenuItems = signal<MenuItem[]>([]);
  // Initialized from localStorage (see loadSortPreference) so the last column/direction the user
  // picked applies from the moment the app loads, in every drive - not just the current session.
  private readonly initialSort = loadSortPreference();
  readonly sortField = signal(this.initialSort.field);
  readonly sortOrder = signal(this.initialSort.order);
  readonly showSearchBox = signal(false);
  readonly searchMode = signal(false);
  readonly searching = signal(false);
  readonly searchResults = signal<FileEntry[]>([]);
  readonly selectionBox = signal<{ left: number; top: number; width: number; height: number } | null>(null);
  readonly folderSizes = signal<ReadonlyMap<string, FolderSizeState>>(new Map());
  // Set after a fresh listing loads so an afterRenderEffect can move real DOM focus onto the
  // first row once it actually exists in the DOM - see the constructor and focusRowElement.
  private readonly pendingFocusPath = signal<string | null>(null);
  // Measured from the actual rendered cells (see measureColumnWidths) so each data column is exactly as
  // wide as its content needs and no wider - any space left over goes to the Name column.
  readonly columnWidths = signal<DataColumnWidths>({
    name: MIN_NAME_COLUMN_WIDTH,
    size: 64,
    type: 112,
    createdAt: 168,
    modifiedAt: 168,
  });
  promptValue = '';
  searchQuery = '';

  private anchorPath: string | null = null;
  private dragPaths: string[] | null = null;
  private queryParamSub?: Subscription;
  // The mountPath the previous query-param navigation resolved to - lets onQueryParamsChanged
  // tell "moved to a different drive" apart from "moved to another folder on the same drive".
  private lastMountPath: string | null = null;
  private rubberBand: { startX: number; startY: number; active: boolean; anchorSelection: FileEntry[] } | null = null;
  private sizeTracker: DirectorySizeTracker;
  private searchDebounceHandle: ReturnType<typeof setTimeout> | null = null;
  private static readonly RUBBER_BAND_THRESHOLD = 4;
  private static readonly SEARCH_DEBOUNCE_MS = 250;
  // Set once the user drags the Name column's resize handle - from then on measureColumnWidths
  // uses this instead of the leftover-space calculation, so the manual size sticks across folders.
  private nameWidthOverride: number | null = null;

  readonly rows = computed<FileEntryRow[]>(() => this.listing()?.entries ?? []);

  readonly sortedRows = computed<FileEntryRow[]>(() => {
    const rows = [...this.rows()];
    const field = this.sortField() as keyof FileEntryRow;
    const order = this.sortOrder();
    const sortValue = (row: FileEntryRow) => (field === 'sizeBytes' ? this.sizeSortValue(row) : row[field]);
    rows.sort((a, b) => {
      const av = sortValue(a);
      const bv = sortValue(b);
      // Nulls (e.g. a directory whose size hasn't finished computing) always sort last, regardless of sort direction.
      if (av == null && bv == null) return 0;
      if (av == null) return 1;
      if (bv == null) return -1;
      const cmp = typeof av === 'string' && typeof bv === 'string' ? av.localeCompare(bv) : av < bv ? -1 : av > bv ? 1 : 0;
      return order * cmp;
    });
    return rows;
  });

  // The Size column shows a directory's live-computed folder size (see folderSizeFor), not
  // entry.sizeBytes - so sorting by size has to read from the same source the cell renders,
  // otherwise directories sort by their (always-null) raw sizeBytes instead of what's on screen.
  private sizeSortValue(row: FileEntryRow): number | null {
    if (!row.isDirectory) {
      return row.sizeBytes;
    }
    const size = this.folderSizeFor(row.path);
    return size && size.status !== 'Running' ? size.bytes : null;
  }

  readonly displayRows = computed<FileEntryRow[]>(() => {
    if (this.searchMode()) {
      return this.searchResults();
    }
    return this.sortedRows();
  });

  readonly formatBytes = formatBytes;
  readonly formatDateTime = formatDateTime;
  readonly fileIconClass = fileIconClass;

  constructor(
    private readonly filesService: FilesService,
    private readonly operationsService: OperationsService,
    private readonly searchService: SearchService,
    private readonly trashService: TrashService,
    private readonly uploadService: UploadService,
    private readonly driveNavState: DriveNavigationStateService,
    private readonly confirmationService: ConfirmationService,
    private readonly messageService: MessageService,
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly elementRef: ElementRef<HTMLElement>,
    private readonly changeDetectorRef: ChangeDetectorRef,
  ) {
    this.sizeTracker = this.createSizeTracker();

    afterRenderEffect(() => {
      this.displayRows();
      this.measureColumnWidths();
    });

    afterRenderEffect(() => {
      const path = this.pendingFocusPath();
      if (!path) {
        return;
      }
      this.focusRowElement(path);
      this.pendingFocusPath.set(null);
    });
  }

  ngOnInit(): void {
    this.queryParamSub = this.route.queryParamMap.subscribe((params) => {
      void this.onQueryParamsChanged(params.get('path') ?? '/');
    });
    // Listening on `document` (rather than this component's own host element) means quick-search
    // typing works even when nothing inside the explorer has taken real DOM focus yet - e.g. right
    // after a folder loads, before the user has clicked a row. A listener on the host element only
    // sees events whose target is a descendant of it, which requires focus to already be inside.
    document.addEventListener('keydown', this.onKeyDown, true);
  }

  ngOnDestroy(): void {
    this.queryParamSub?.unsubscribe();
    document.removeEventListener('keydown', this.onKeyDown, true);
    window.removeEventListener('mousemove', this.onRubberBandMove);
    window.removeEventListener('mouseup', this.onRubberBandUp);
    this.sizeTracker.dispose();
    if (this.searchDebounceHandle !== null) {
      clearTimeout(this.searchDebounceHandle);
    }
  }

  get breadcrumb() {
    return pathSegments(this.listing()?.path ?? '/');
  }

  navigateTo(path: string): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: { path } });
  }

  // Runs on every path change coming from the URL - folder navigation, breadcrumb clicks, and
  // drive switches all go through here. Only a drive switch (mountPath actually changes) saves
  // and restores that drive's search query; plain in-drive navigation just clears the search UI,
  // same as before, so browsing normally doesn't resurrect a search you just backed out of.
  private async onQueryParamsChanged(path: string): Promise<void> {
    const previousMountPath = this.lastMountPath;
    if (previousMountPath && this.searchMode() && this.searchQuery.trim()) {
      this.driveNavState.saveSearch(previousMountPath, this.searchQuery);
    }
    this.resetSearchUi();

    await this.performLoad(path);

    this.driveNavState.recordVisit(path);

    const mountPath = this.driveNavState.mountPathFor(path);
    this.lastMountPath = mountPath;
    if (!mountPath) {
      return;
    }
    if (mountPath !== previousMountPath) {
      const rememberedQuery = this.driveNavState.getSearch(mountPath);
      if (rememberedQuery) {
        this.searchQuery = rememberedQuery;
        this.showSearchBox.set(true);
        await this.runSearch();
      }
    }
  }

  private async performLoad(path: string): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set(null);
    this.resetSizeTracker();
    try {
      const listing = await this.filesService.list(path);
      this.listing.set(listing);
      const firstRow = this.sortedRows().at(0) ?? null;
      this.selection.set(firstRow ? [firstRow] : []);
      this.focusedPath.set(firstRow?.path ?? null);
      this.anchorPath = firstRow?.path ?? null;
      this.trackVisibleFolderSizes();
      this.pendingFocusPath.set(firstRow?.path ?? null);
    } catch {
      this.errorMessage.set(`Could not open "${path}".`);
    } finally {
      this.loading.set(false);
    }
  }

  goUp(): void {
    const parent = this.listing()?.parentPath;
    if (parent) {
      this.navigateTo(parent);
    }
  }

  refresh(): void {
    void this.performLoad(this.listing()?.path ?? '/');
  }

  onRowActivate(entry: FileEntry): void {
    if (entry.isDirectory) {
      this.navigateTo(entry.path);
    } else {
      this.openPreview(entry);
    }
  }

  // ---- Folder sizes ----

  folderSizeFor(path: string): FolderSizeState | undefined {
    return this.folderSizes().get(path);
  }

  private trackVisibleFolderSizes(): void {
    for (const row of this.displayRows()) {
      if (row.isDirectory) {
        this.sizeTracker.track(row.path);
      }
    }
  }

  private createSizeTracker(): DirectorySizeTracker {
    return new DirectorySizeTracker(this.filesService, (state) => this.folderSizes.set(new Map(state)));
  }

  private resetSizeTracker(): void {
    this.sizeTracker.dispose();
    this.sizeTracker = this.createSizeTracker();
    this.folderSizes.set(new Map());
  }

  // ---- Column sizing ----
  //
  // table-layout: fixed keeps a long file name from squeezing the data columns, but that means *we*
  // have to size those columns instead of the browser. Cloning the live table with table-layout: auto
  // and reading the resulting header widths gets an exact answer - including things a manual estimate
  // would get wrong, like tabular-nums digit width on the Size column - without duplicating the
  // formatting/rendering logic. The clone is measured off-screen and discarded immediately.
  private measureColumnWidths(): void {
    const table = this.elementRef.nativeElement.querySelector('.p-datatable-table') as HTMLTableElement | null;
    if (!table) {
      return;
    }

    const clone = table.cloneNode(true) as HTMLTableElement;
    clone.style.position = 'absolute';
    clone.style.visibility = 'hidden';
    clone.style.left = '-99999px';
    clone.style.top = '0';
    clone.style.tableLayout = 'auto';
    clone.style.width = 'auto';
    clone.querySelectorAll<HTMLElement>('thead th').forEach((th) => (th.style.width = 'auto'));

    document.body.appendChild(clone);
    const widths = [...clone.querySelectorAll('thead th')].map((th) => Math.ceil(th.getBoundingClientRect().width));
    document.body.removeChild(clone);

    if (widths.length < 5) {
      return;
    }
    const [, size, type, createdAt, modifiedAt] = widths;

    // table-layout: fixed sizes an unspecified column from its own content width, *not* from the
    // container's remaining space, and ignores min-width/max-width on its cells entirely - so the
    // Name column has to get an explicit width too, computed as whatever's left over, or it can end
    // up sized to little more than its header text. table.parentElement is the `#wrapper` div
    // PrimeNG renders around the table (.p-datatable-table-container), which is sized by ordinary
    // flex layout rather than the table's own fixed column algorithm.
    const containerWidth = table.parentElement?.clientWidth ?? table.clientWidth;
    const name =
      this.nameWidthOverride != null
        ? Math.max(MIN_NAME_COLUMN_WIDTH, this.nameWidthOverride)
        : Math.max(MIN_NAME_COLUMN_WIDTH, containerWidth - size - type - createdAt - modifiedAt);

    this.columnWidths.set({ name, size, type, createdAt, modifiedAt });
  }

  // Drag-to-resize for the Name column header. Other columns stay auto-measured from their content
  // (see measureColumnWidths); Name is the one column wide enough, and free-form enough in content,
  // to be worth letting the user size by hand.
  onNameColumnResizeStart(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();

    const startX = event.clientX;
    const startWidth = this.columnWidths().name;

    const onMouseMove = (moveEvent: MouseEvent): void => {
      const width = Math.max(MIN_NAME_COLUMN_WIDTH, startWidth + (moveEvent.clientX - startX));
      this.nameWidthOverride = width;
      this.columnWidths.update((widths) => ({ ...widths, name: width }));
    };

    const onMouseUp = (): void => {
      window.removeEventListener('mousemove', onMouseMove);
      window.removeEventListener('mouseup', onMouseUp);
    };

    window.addEventListener('mousemove', onMouseMove);
    window.addEventListener('mouseup', onMouseUp);
  }

  // ---- Keyboard navigation ----
  //
  // PrimeNG's [pSelectableRow] directive attaches its own keydown handler directly to each
  // row (for accessibility), which fires before any bubble-phase listener on this component
  // and fights over selection state. Listening in the capture phase - and stopping
  // propagation for the keys we own - lets us take those keys before that handler ever sees
  // them, instead of a `@HostListener('keydown')` (bubble phase, fires too late).

  private readonly onKeyDown = (event: KeyboardEvent): void => {
    const target = event.target as HTMLElement;
    if (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA') {
      return;
    }
    // This listener is on `document` (see ngOnInit) so quick-search still works before anything
    // inside the explorer has taken real DOM focus - but that means it must not hijack keys meant
    // for a control focused elsewhere in the app (e.g. Enter on a sidebar drive button). Only handle
    // the event when focus is inside the explorer itself, or nowhere in particular (document.body).
    if (target !== document.body && !this.elementRef.nativeElement.contains(target)) {
      return;
    }
    if (this.promptDialog() || this.propertiesVisible() || this.previewVisible()) {
      return;
    }
    // The delete confirmation dialog (and any other PrimeNG dialog/overlay) isn't tracked by a
    // local signal here, so detect it generically: if focus is inside a dialog/alertdialog (e.g.
    // the confirm dialog's Yes/No buttons), let the browser's native Enter/Space button-activation
    // behavior run instead of stealing the key for row navigation or quick search.
    if (target.closest('[role="dialog"], [role="alertdialog"]')) {
      return;
    }

    const ctrl = event.ctrlKey || event.metaKey;

    if (ctrl) {
      switch (event.key) {
        case 'a':
        case 'A':
          event.preventDefault();
          event.stopPropagation();
          this.selection.set([...this.displayRows()]);
          break;
        case 'c':
        case 'C':
          event.preventDefault();
          event.stopPropagation();
          this.copySelection();
          break;
        case 'x':
        case 'X':
          event.preventDefault();
          event.stopPropagation();
          this.cutSelection();
          break;
        case 'v':
        case 'V':
          event.preventDefault();
          event.stopPropagation();
          void this.paste();
          break;
      }
      return;
    }

    const ownedKeys = ['ArrowDown', 'ArrowUp', 'PageDown', 'PageUp', 'Enter', 'Backspace', 'Delete', 'F2', 'Escape'];
    if (ownedKeys.includes(event.key)) {
      event.stopPropagation();
    }

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.moveFocus(1, event.shiftKey);
        break;
      case 'ArrowUp':
        event.preventDefault();
        if (event.altKey) {
          this.goUp();
        } else {
          this.moveFocus(-1, event.shiftKey);
        }
        break;
      case 'PageDown':
        event.preventDefault();
        this.moveFocus(Infinity, event.shiftKey);
        break;
      case 'PageUp':
        event.preventDefault();
        this.moveFocus(-Infinity, event.shiftKey);
        break;
      case 'Enter':
        event.preventDefault();
        this.activateFocused();
        break;
      case 'Backspace':
        event.preventDefault();
        this.goUp();
        break;
      case 'Delete':
        event.preventDefault();
        this.confirmDelete();
        break;
      case 'F2':
        event.preventDefault();
        this.openRename();
        break;
      case 'Escape':
        this.selection.set([]);
        break;
      default:
        // Any other printable character starts (or continues) an instant search, so the user
        // doesn't have to click the search icon and hit Enter just to filter the current folder.
        if (!event.altKey && event.key.length === 1) {
          event.preventDefault();
          event.stopPropagation();
          this.startQuickSearch(event.key);
        }
        break;
    }
  };

  private startQuickSearch(char: string): void {
    if (!this.showSearchBox()) {
      this.searchQuery = '';
      this.showSearchBox.set(true);
    }
    this.searchQuery += char;
    this.scheduleSearch();
    // The search input only exists in the DOM once change detection processes the
    // `@if (showSearchBox())` block, so `searchInputRef` isn't populated yet on the
    // keystroke that first reveals it. Force a synchronous check here so the ViewChild
    // (and its ngModel-bound value) is up to date before we try to focus it.
    this.changeDetectorRef.detectChanges();
    this.focusSearchInput();
  }

  private focusSearchInput(): void {
    const el = this.searchInputRef?.nativeElement;
    if (!el) {
      return;
    }
    el.focus();
    const end = el.value.length;
    el.setSelectionRange(end, end);
  }

  scheduleSearch(): void {
    if (this.searchDebounceHandle !== null) {
      clearTimeout(this.searchDebounceHandle);
    }
    this.searchDebounceHandle = setTimeout(() => {
      this.searchDebounceHandle = null;
      void this.runSearch();
    }, ExplorerComponent.SEARCH_DEBOUNCE_MS);
  }

  private moveFocus(delta: number, extend: boolean): void {
    const rows = this.displayRows();
    if (rows.length === 0) {
      return;
    }
    const currentPath = this.focusedPath() ?? this.selection().at(-1)?.path ?? rows[0].path;
    const currentIndex = Math.max(
      0,
      rows.findIndex((r) => r.path === currentPath),
    );
    const nextIndex = Math.min(rows.length - 1, Math.max(0, currentIndex + delta));
    const nextRow = rows[nextIndex];
    this.focusedPath.set(nextRow.path);

    if (extend) {
      this.anchorPath ??= currentPath;
      this.selectRange(this.anchorPath, nextRow.path);
    } else {
      this.anchorPath = nextRow.path;
      this.selection.set([nextRow]);
    }
    this.scrollRowIntoView(nextRow.path);
  }

  private selectRange(fromPath: string, toPath: string): void {
    const rows = this.displayRows();
    const fromIndex = rows.findIndex((r) => r.path === fromPath);
    const toIndex = rows.findIndex((r) => r.path === toPath);
    if (fromIndex === -1 || toIndex === -1) {
      return;
    }
    const [start, end] = fromIndex <= toIndex ? [fromIndex, toIndex] : [toIndex, fromIndex];
    this.selection.set(rows.slice(start, end + 1));
  }

  private scrollRowIntoView(path: string): void {
    queueMicrotask(() => {
      const escaped = typeof CSS !== 'undefined' ? CSS.escape(path) : path;
      const el = this.elementRef.nativeElement.querySelector(`[data-row-path="${escaped}"]`);
      el?.scrollIntoView({ block: 'nearest' });
    });
  }

  // Moves real DOM focus onto a row (called after a fresh listing loads - see pendingFocusPath)
  // so arrow-key navigation keeps working immediately, without requiring the user to click into
  // the list first. Skipped while focus is genuinely needed elsewhere (a text input or a dialog)
  // so this doesn't steal focus mid-rename or mid-search.
  private focusRowElement(path: string): void {
    const active = document.activeElement as HTMLElement | null;
    if (active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA' || active.closest('[role="dialog"], [role="alertdialog"]'))) {
      return;
    }
    const escaped = typeof CSS !== 'undefined' ? CSS.escape(path) : path;
    const el = this.elementRef.nativeElement.querySelector<HTMLElement>(`[data-row-path="${escaped}"]`);
    el?.focus();
  }

  private activateFocused(): void {
    const path = this.focusedPath() ?? this.selection().at(-1)?.path;
    const row = this.displayRows().find((r) => r.path === path);
    if (row) {
      this.onRowActivate(row);
    }
  }

  onSort(event: { field?: string; order?: number }): void {
    if (event.field) {
      const order = event.order ?? 1;
      this.sortField.set(event.field);
      this.sortOrder.set(order);
      saveSortPreference({ field: event.field, order });
    }
  }

  onRowClick(entry: FileEntryRow): void {
    this.focusedPath.set(entry.path);
    this.anchorPath = entry.path;
  }

  // ---- Context menu ----

  onRowContextMenu(event: MouseEvent, entry: FileEntryRow): void {
    event.preventDefault();
    event.stopPropagation();
    if (!this.selection().some((s) => s.path === entry.path)) {
      this.selection.set([entry]);
    }
    this.focusedPath.set(entry.path);
    this.anchorPath = entry.path;
    this.contextMenuItems.set(this.buildRowMenu());
    this.rowMenu.show(event);
  }

  onEmptyContextMenu(event: MouseEvent): void {
    event.preventDefault();
    this.selection.set([]);
    this.contextMenuItems.set(this.buildEmptyMenu());
    this.rowMenu.show(event);
  }

  private buildRowMenu(): MenuItem[] {
    const selected = this.selection();
    const single = selected.length === 1 ? selected[0] : null;
    const items: MenuItem[] = [];

    if (single?.isDirectory) {
      items.push({ label: 'Open', icon: 'pi pi-folder-open', command: () => this.onRowActivate(single) });
    } else if (single) {
      items.push({ label: 'Preview', icon: 'pi pi-eye', command: () => this.openPreview(single) });
    }

    items.push(
      { separator: true },
      { label: 'Cut', icon: 'pi pi-arrows-alt', command: () => this.cutSelection() },
      { label: 'Copy', icon: 'pi pi-copy', command: () => this.copySelection() },
      { label: 'Download', icon: 'pi pi-download', command: () => void this.download() },
      { separator: true },
      { label: 'Rename', icon: 'pi pi-pencil', disabled: selected.length !== 1, command: () => this.openRename() },
      { label: 'Delete', icon: 'pi pi-trash', command: () => this.confirmDelete() },
      { separator: true },
      { label: 'Properties', icon: 'pi pi-info-circle', command: () => this.openProperties() },
    );
    return items;
  }

  private buildEmptyMenu(): MenuItem[] {
    return [
      { label: 'New folder', icon: 'pi pi-folder-plus', command: () => this.openNewFolder() },
      { label: 'New file', icon: 'pi pi-file-plus', command: () => this.openNewFile() },
      { separator: true },
      { label: 'Paste', icon: 'pi pi-clipboard', disabled: !this.clipboard(), command: () => void this.paste() },
      { separator: true },
      { label: 'Upload files', icon: 'pi pi-upload', command: () => this.openUploadFilesPicker() },
      { label: 'Upload folder', icon: 'pi pi-cloud-upload', command: () => this.openUploadFolderPicker() },
    ];
  }

  // ---- Properties & preview ----

  openProperties(): void {
    if (this.selection().length === 0) {
      return;
    }
    this.propertiesEntries.set([...this.selection()]);
    this.propertiesVisible.set(true);
  }

  openPreview(entry: FileEntry): void {
    this.previewEntry.set(entry);
    this.previewVisible.set(true);
  }

  // ---- Search ----

  toggleSearchBox(): void {
    this.showSearchBox.update((v) => !v);
    if (!this.showSearchBox()) {
      this.exitSearch();
    } else {
      this.changeDetectorRef.detectChanges();
      this.focusSearchInput();
    }
  }

  onSearchEnter(): void {
    if (this.searchDebounceHandle !== null) {
      clearTimeout(this.searchDebounceHandle);
      this.searchDebounceHandle = null;
    }
    void this.runSearch();
  }

  async runSearch(): Promise<void> {
    const query = this.searchQuery.trim();
    if (!query) {
      this.exitSearch();
      return;
    }
    this.searching.set(true);
    this.searchMode.set(true);
    try {
      this.searchResults.set(await this.searchService.search(this.listing()?.path ?? '/', query));
    } catch {
      this.searchResults.set([]);
    } finally {
      this.searching.set(false);
    }
  }

  // User-facing "stop searching" - unlike resetSearchUi, this also forgets the drive's
  // remembered search query so switching away and back doesn't bring it back.
  exitSearch(): void {
    this.resetSearchUi();
    const mountPath = this.driveNavState.mountPathFor(this.listing()?.path ?? '/');
    if (mountPath) {
      this.driveNavState.clearSearch(mountPath);
    }
  }

  private resetSearchUi(): void {
    if (this.searchDebounceHandle !== null) {
      clearTimeout(this.searchDebounceHandle);
      this.searchDebounceHandle = null;
    }
    this.searchMode.set(false);
    this.searchResults.set([]);
    this.searchQuery = '';
  }

  // ---- Rubber-band multiselect ----

  onTableMouseDown(event: MouseEvent): void {
    if (event.button !== 0) {
      return;
    }
    // A mousedown on the Name cell is reserved for native drag-and-drop (see the draggable
    // `<td>` in the template); starting a rubber-band drag there would fight the browser's own
    // drag gesture. Mousedown on any other cell (size/type/date columns, or empty table space)
    // begins rubber-band multiselect instead - single clicks are unaffected since selection
    // there is still driven by PrimeNG's own click handling on the row.
    const target = event.target as HTMLElement;
    if (target.closest('.explorer__name-cell') || target.closest('th') || target.closest('button') || target.closest('a')) {
      return;
    }
    event.preventDefault();
    this.rubberBand = {
      startX: event.clientX,
      startY: event.clientY,
      active: false,
      anchorSelection: event.ctrlKey || event.metaKey || event.shiftKey ? [...this.selection()] : [],
    };
    window.addEventListener('mousemove', this.onRubberBandMove);
    window.addEventListener('mouseup', this.onRubberBandUp);
  }

  private readonly onRubberBandMove = (event: MouseEvent): void => {
    const band = this.rubberBand;
    if (!band) {
      return;
    }
    const dx = event.clientX - band.startX;
    const dy = event.clientY - band.startY;
    if (!band.active && Math.hypot(dx, dy) < ExplorerComponent.RUBBER_BAND_THRESHOLD) {
      return;
    }
    band.active = true;
    event.preventDefault();

    const left = Math.min(band.startX, event.clientX);
    const top = Math.min(band.startY, event.clientY);
    const width = Math.abs(dx);
    const height = Math.abs(dy);
    this.selectionBox.set({ left, top, width, height });

    const rect = { left, top, right: left + width, bottom: top + height };
    const matched = this.displayRows().filter((row) => {
      const escaped = typeof CSS !== 'undefined' ? CSS.escape(row.path) : row.path;
      const el = this.elementRef.nativeElement.querySelector(`[data-row-path="${escaped}"]`);
      if (!el) {
        return false;
      }
      const r = el.getBoundingClientRect();
      return r.left < rect.right && r.right > rect.left && r.top < rect.bottom && r.bottom > rect.top;
    });

    const merged = [...band.anchorSelection];
    for (const row of matched) {
      if (!merged.some((s) => s.path === row.path)) {
        merged.push(row);
      }
    }
    this.selection.set(merged);
  };

  private readonly onRubberBandUp = (): void => {
    this.rubberBand = null;
    this.selectionBox.set(null);
    window.removeEventListener('mousemove', this.onRubberBandMove);
    window.removeEventListener('mouseup', this.onRubberBandUp);
  };

  // ---- Drag & drop ----

  onDragStart(event: DragEvent, entry: FileEntryRow): void {
    const selected = this.selection();
    const paths = selected.some((s) => s.path === entry.path) ? selected.map((s) => s.path) : [entry.path];
    this.dragPaths = paths;
    event.dataTransfer?.setData('text/plain', JSON.stringify(paths));
    if (event.dataTransfer) {
      event.dataTransfer.effectAllowed = 'copyMove';
    }
  }

  onRowDragOver(event: DragEvent, entry: FileEntryRow): void {
    if (!entry.isDirectory) {
      return;
    }
    if (this.isExternalFileDrag(event)) {
      event.preventDefault();
      // Stop this from also reaching the table-wrap's own dragover handler - otherwise, once a drop lands,
      // both this row's handler and the table-wide "drop into current folder" handler would fire for it.
      event.stopPropagation();
      return;
    }
    if (!this.dragPaths) {
      return;
    }
    event.preventDefault();
    if (event.dataTransfer) {
      event.dataTransfer.dropEffect = event.ctrlKey ? 'copy' : 'move';
    }
  }

  async onRowDrop(event: DragEvent, entry: FileEntryRow): Promise<void> {
    event.preventDefault();
    if (!entry.isDirectory) {
      this.dragPaths = null;
      return;
    }
    if (this.isExternalFileDrag(event)) {
      // Without this, the drop event bubbles from the row up to the table-wrap's own drop handler, which
      // would upload the same files a second time into the currently open folder.
      event.stopPropagation();
      await this.handleExternalDrop(event, entry.path);
      return;
    }

    const dragPaths = this.dragPaths;
    this.dragPaths = null;
    if (!dragPaths) {
      return;
    }
    const paths = dragPaths.filter((p) => p !== entry.path);
    if (paths.length === 0) {
      return;
    }
    const mode = event.ctrlKey ? 'Copy' : 'Move';
    await this.operationsService.create(mode, paths, entry.path);
    this.messageService.add({
      severity: 'info',
      summary: mode === 'Copy' ? 'Copying' : 'Moving',
      detail: 'Started in background - see Tasks for progress.',
    });
    setTimeout(() => this.refresh(), 600);
  }

  // ---- Upload ----

  private isExternalFileDrag(event: DragEvent): boolean {
    return !this.dragPaths && !!event.dataTransfer?.types.includes('Files');
  }

  onTableDragOver(event: DragEvent): void {
    if (this.isExternalFileDrag(event)) {
      event.preventDefault();
    }
  }

  async onTableDrop(event: DragEvent): Promise<void> {
    if (!this.isExternalFileDrag(event)) {
      return;
    }
    event.preventDefault();
    await this.handleExternalDrop(event, this.listing()?.path ?? '/');
  }

  private async handleExternalDrop(event: DragEvent, destinationPath: string): Promise<void> {
    if (!event.dataTransfer) {
      return;
    }
    const entries = await collectFilesFromDataTransfer(event.dataTransfer);
    await this.startUpload(entries, destinationPath);
  }

  openUploadFilesPicker(): void {
    this.fileInputRef?.nativeElement.click();
  }

  openUploadFolderPicker(): void {
    this.folderInputRef?.nativeElement.click();
  }

  async onFileInputChange(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    if (input.files?.length) {
      await this.startUpload(collectFilesFromFileList(input.files), this.listing()?.path ?? '/');
    }
    input.value = '';
  }

  async onFolderInputChange(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    if (input.files?.length) {
      await this.startUpload(collectFilesFromFileList(input.files), this.listing()?.path ?? '/');
    }
    input.value = '';
  }

  private async startUpload(entries: { file: File; relativePath: string }[], destinationPath: string): Promise<void> {
    if (entries.length === 0) {
      return;
    }
    await this.uploadService.startUpload(entries, destinationPath);
    if (destinationPath === (this.listing()?.path ?? '/')) {
      this.refresh();
    }
  }

  // ---- Download ----

  async download(): Promise<void> {
    const selected = this.selection();
    if (selected.length === 0) {
      return;
    }

    if (selected.length === 1 && !selected[0].isDirectory) {
      await downloadBlob('/files/download', { path: selected[0].path }, selected[0].name);
      return;
    }

    try {
      const paths = selected.map((entry) => entry.path);
      await this.operationsService.create('Zip', paths);
      this.messageService.add({
        severity: 'info',
        summary: 'Preparing download',
        detail: 'Zipping - see Tasks for progress and to download when ready.',
      });
    } catch (error) {
      const spaceInfo = getInsufficientSpaceInfo(error);
      if (spaceInfo) {
        this.messageService.add({
          severity: 'error',
          summary: 'Not enough space to build the zip',
          detail: `This needs about ${formatBytes(spaceInfo.requiredBytes)} but only ${formatBytes(spaceInfo.availableBytes)} is free. Free up some space, or download the items one at a time instead.`,
          sticky: true,
        });
      } else {
        this.messageService.add({ severity: 'error', summary: 'Failed', detail: 'Could not start the download.' });
      }
    }
  }

  // ---- Create / rename ----

  openNewFolder(): void {
    this.openPrompt('New folder', 'Folder name', '', async (name) => {
      await this.filesService.createEntry(this.listing()?.path ?? '/', name, true);
      this.refresh();
    });
  }

  openNewFile(): void {
    this.openPrompt('New file', 'File name', '', async (name) => {
      await this.filesService.createEntry(this.listing()?.path ?? '/', name, false);
      this.refresh();
    });
  }

  openRename(): void {
    const entry = this.selection()[0];
    if (!entry) {
      return;
    }
    this.openPrompt('Rename', 'New name', entry.name, async (name) => {
      await this.filesService.rename(entry.path, name);
      this.refresh();
    });
  }

  private openPrompt(title: string, label: string, initialValue: string, onConfirm: (value: string) => Promise<void>): void {
    this.promptValue = initialValue;
    this.promptDialog.set({ title, label, onConfirm });
  }

  async confirmPrompt(): Promise<void> {
    const prompt = this.promptDialog();
    if (!prompt || !this.promptValue.trim()) {
      return;
    }
    try {
      await prompt.onConfirm(this.promptValue.trim());
      this.promptDialog.set(null);
    } catch (error) {
      this.messageService.add({ severity: 'error', summary: 'Failed', detail: this.extractError(error) });
    }
  }

  cancelPrompt(): void {
    this.promptDialog.set(null);
  }

  // ---- Delete / clipboard ----

  confirmDelete(): void {
    const paths = this.selection().map((entry) => entry.path);
    if (paths.length === 0) {
      return;
    }
    this.pendingDeletePaths.set(paths);
    this.confirmationService.confirm({
      key: 'delete',
      header: 'Delete',
      message: `Move ${paths.length} item(s) to the Trash?`,
      icon: 'pi pi-exclamation-triangle',
      acceptButtonProps: { severity: 'danger', label: 'Delete' },
      accept: () => void this.handleDeleteConfirmed(paths),
    });
  }

  deletePermanentlyFromDialog(): void {
    const paths = this.pendingDeletePaths();
    if (paths.length === 0) {
      return;
    }
    void this.deleteSelection(paths, true);
  }

  private async handleDeleteConfirmed(paths: string[]): Promise<void> {
    let hasSpace: boolean;
    try {
      hasSpace = await this.trashService.checkSpace(paths);
    } catch {
      // Don't block the delete on a failed space check - the job itself falls back to a permanent
      // delete if it turns out there really isn't room.
      hasSpace = true;
    }

    if (hasSpace) {
      await this.deleteSelection(paths, false);
      return;
    }

    this.confirmationService.confirm({
      header: 'Not enough space for Trash',
      message: `There isn't enough free space to move ${paths.length} item(s) to the Trash. Delete them permanently instead? This cannot be undone.`,
      icon: 'pi pi-exclamation-triangle',
      acceptButtonProps: { severity: 'danger', label: 'Delete permanently' },
      accept: () => void this.deleteSelection(paths, true),
    });
  }

  private async deleteSelection(paths: string[], permanent: boolean): Promise<void> {
    await this.operationsService.create('Delete', paths, undefined, permanent);
    this.messageService.add({
      severity: 'info',
      summary: 'Deleting',
      detail: permanent ? 'Permanently deleting - see Tasks for progress.' : 'Moved to Trash - see Tasks for progress.',
    });
    const deleted = new Set(paths);
    this.selection.set(this.selection().filter((entry) => !deleted.has(entry.path)));
    if (this.searchMode()) {
      this.searchResults.set(this.searchResults().filter((entry) => !deleted.has(entry.path)));
    }
    const current = this.listing();
    if (current) {
      this.listing.set({ ...current, entries: current.entries.filter((entry) => !deleted.has(entry.path)) });
    }
    setTimeout(() => this.refresh(), 600);
  }

  copySelection(): void {
    const paths = this.selection().map((entry) => entry.path);
    if (paths.length > 0) {
      this.clipboard.set({ mode: 'copy', paths });
    }
  }

  cutSelection(): void {
    const paths = this.selection().map((entry) => entry.path);
    if (paths.length > 0) {
      this.clipboard.set({ mode: 'cut', paths });
    }
  }

  async paste(): Promise<void> {
    const clip = this.clipboard();
    const destination = this.listing()?.path;
    if (!clip || !destination || clip.paths.length === 0) {
      return;
    }

    await this.operationsService.create(clip.mode === 'copy' ? 'Copy' : 'Move', clip.paths, destination);
    this.messageService.add({
      severity: 'info',
      summary: clip.mode === 'copy' ? 'Copying' : 'Moving',
      detail: 'Started in background - see Tasks for progress.',
    });
    if (clip.mode === 'cut') {
      this.clipboard.set(null);
    }
    setTimeout(() => this.refresh(), 600);
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
