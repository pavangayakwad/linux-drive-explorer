import { CommonModule } from '@angular/common';
import { Component, computed, ElementRef, EventEmitter, HostListener, Input, Output, QueryList, ViewChildren, signal } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { ConfirmationService, MenuItem } from 'primeng/api';
import { MenuModule } from 'primeng/menu';
import { TooltipModule } from 'primeng/tooltip';
import { AuthService } from '../../../core/services/auth.service';
import { ThemeService, THEME_COLORS, THEME_MODES, ThemeMode } from '../../../core/services/theme.service';
import { ThemeColor } from '../../../core/models/auth.model';
import { DriveSummary, UnmountedDevice } from '../../../core/models/file-system.model';
import { DriveNavigationStateService } from '../../../core/services/drive-navigation-state.service';
import { formatBytes } from '../../format.util';
import { ChangePasswordDialogComponent } from '../change-password-dialog/change-password-dialog.component';

const THEME_COLOR_LABELS: Record<ThemeColor, string> = {
  green: 'Green',
  blue: 'Blue',
  violet: 'Violet',
};

const THEME_MODE_LABELS: Record<ThemeMode, string> = {
  light: 'Light',
  dark: 'Dark',
};

const THEME_MODE_ICONS: Record<ThemeMode, string> = {
  light: 'pi pi-sun',
  dark: 'pi pi-moon',
};

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive, MenuModule, TooltipModule, ChangePasswordDialogComponent],
  templateUrl: './sidebar.component.html',
  styleUrl: './sidebar.component.scss',
})
export class SidebarComponent {
  @Input() drives: DriveSummary[] = [];
  @Input() unmountedDevices: UnmountedDevice[] = [];
  @Input() currentPath = '/';
  @Input() refreshingDrives = false;
  @Input() mountingDevice: string | null = null;
  @Input() unmountingPath: string | null = null;
  @Input() runningTaskCount = 0;
  @Output() readonly navigate = new EventEmitter<string>();
  @Output() readonly refreshDrives = new EventEmitter<void>();
  @Output() readonly mountDevice = new EventEmitter<string>();
  @Output() readonly unmountDrive = new EventEmitter<string>();

  @ViewChildren('driveItem') private readonly driveItems?: QueryList<ElementRef<HTMLButtonElement>>;

  readonly changePasswordVisible = signal(false);

  // PrimeNG's p-menu switches its ENTIRE model to grouped/submenu rendering as soon as any
  // item has a nested `items` array (see Menu.hasSubMenu()) - plain command items without
  // `items` then render as inert `role="none"` headers instead of clickable menuitems. Keep
  // this model flat (no nested `items`) so every entry stays a real, clickable menuitem.
  readonly userMenuItems = computed<MenuItem[]>(() => [
    { label: 'Change password', icon: 'pi pi-key', command: () => this.changePasswordVisible.set(true) },
    { separator: true },
    { label: 'Theme color', disabled: true },
    ...THEME_COLORS.map((color) => ({
      label: THEME_COLOR_LABELS[color],
      icon: this.themeService.current() === color ? 'pi pi-check' : 'pi pi-circle-fill',
      styleClass: `sidebar__theme-item sidebar__theme-item--${color}`,
      command: () => void this.themeService.select(color),
    })),
    { separator: true },
    { label: 'Appearance', disabled: true },
    ...THEME_MODES.map((mode) => ({
      label: THEME_MODE_LABELS[mode],
      icon: this.themeService.mode() === mode ? 'pi pi-check' : THEME_MODE_ICONS[mode],
      command: () => this.themeService.applyMode(mode),
    })),
    { separator: true },
    { label: 'Sign out', icon: 'pi pi-sign-out', command: () => this.logout() },
  ]);

  readonly formatBytes = formatBytes;

  constructor(
    readonly authService: AuthService,
    private readonly themeService: ThemeService,
    private readonly driveNavState: DriveNavigationStateService,
    private readonly confirmationService: ConfirmationService,
  ) {}

  logout(): void {
    void this.authService.logout();
  }

  // Alt+1 jumps focus straight to the drives panel (the currently active drive if one is
  // visible, otherwise the first drive) so keyboard users don't have to Tab through the rest
  // of the sidebar to get there.
  @HostListener('document:keydown', ['$event'])
  onGlobalKeyDown(event: KeyboardEvent): void {
    if (!event.altKey || event.key !== '1') {
      return;
    }
    const buttons = this.driveItems?.toArray() ?? [];
    if (buttons.length === 0) {
      return;
    }
    event.preventDefault();
    const activeIndex = this.drives.findIndex((drive) => this.isActive(drive));
    buttons[activeIndex >= 0 ? activeIndex : 0].nativeElement.focus();
  }

  // Lets arrow keys move focus between drive entries the same way Tab already does, instead of
  // only supporting Tab-order navigation once focus has landed in the drives list (e.g. via Alt+1).
  onDriveKeyDown(event: KeyboardEvent, index: number): void {
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') {
      return;
    }
    const buttons = this.driveItems?.toArray() ?? [];
    if (buttons.length === 0) {
      return;
    }
    event.preventDefault();
    const delta = event.key === 'ArrowDown' ? 1 : -1;
    const nextIndex = Math.min(buttons.length - 1, Math.max(0, index + delta));
    buttons[nextIndex].nativeElement.focus();
  }

  usedFraction(drive: DriveSummary): number {
    if (drive.totalBytes <= 0) {
      return 0;
    }
    return (drive.totalBytes - drive.freeBytes) / drive.totalBytes;
  }

  isActive(drive: DriveSummary): boolean {
    return this.currentPath === drive.mountPath || this.currentPath.startsWith(`${drive.mountPath}/`);
  }

  // The backend only ever allows unmounting drives under /mnt (see HostMountService.UnmountAsync) -
  // a removable drive the OS auto-mounted elsewhere (e.g. /run/media/$USER/<label> on udisks2
  // distros, /media/$USER/<label> on Debian/Ubuntu) is browsable but not something this app can eject.
  canUnmount(drive: DriveSummary): boolean {
    return drive.isRemovable && (drive.mountPath === '/mnt' || drive.mountPath.startsWith('/mnt/'));
  }

  select(drive: DriveSummary): void {
    // Re-selecting a drive that's already been visited this session returns to the folder (and,
    // via ExplorerComponent, the search) the user last left it at, rather than resetting to root.
    this.navigate.emit(this.driveNavState.getPath(drive.mountPath) ?? drive.mountPath);
  }

  confirmUnmount(event: MouseEvent, drive: DriveSummary): void {
    event.stopPropagation();
    this.confirmationService.confirm({
      target: event.currentTarget as EventTarget,
      header: 'Eject drive',
      message: `Eject "${drive.name}"? Make sure no files on it are open elsewhere first.`,
      icon: 'pi pi-exclamation-triangle',
      acceptButtonProps: { severity: 'danger', label: 'Eject' },
      accept: () => this.unmountDrive.emit(drive.mountPath),
    });
  }
}
