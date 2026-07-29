import { CommonModule } from '@angular/common';
import { Component, computed, EventEmitter, Input, Output, signal } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { MenuItem } from 'primeng/api';
import { MenuModule } from 'primeng/menu';
import { TooltipModule } from 'primeng/tooltip';
import { AuthService } from '../../../core/services/auth.service';
import { ThemeService, THEME_COLORS } from '../../../core/services/theme.service';
import { ThemeColor } from '../../../core/models/auth.model';
import { DriveSummary } from '../../../core/models/file-system.model';
import { DriveNavigationStateService } from '../../../core/services/drive-navigation-state.service';
import { formatBytes } from '../../format.util';
import { ChangePasswordDialogComponent } from '../change-password-dialog/change-password-dialog.component';

const THEME_COLOR_LABELS: Record<ThemeColor, string> = {
  green: 'Green',
  blue: 'Blue',
  violet: 'Violet',
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
  @Input() currentPath = '/';
  @Input() refreshingDrives = false;
  @Input() runningTaskCount = 0;
  @Output() readonly navigate = new EventEmitter<string>();
  @Output() readonly refreshDrives = new EventEmitter<void>();

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
    { label: 'Sign out', icon: 'pi pi-sign-out', command: () => this.logout() },
  ]);

  readonly formatBytes = formatBytes;

  constructor(
    readonly authService: AuthService,
    private readonly themeService: ThemeService,
    private readonly driveNavState: DriveNavigationStateService,
  ) {}

  logout(): void {
    void this.authService.logout();
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

  select(drive: DriveSummary): void {
    // Re-selecting a drive that's already been visited this session returns to the folder (and,
    // via ExplorerComponent, the search) the user last left it at, rather than resetting to root.
    this.navigate.emit(this.driveNavState.getPath(drive.mountPath) ?? drive.mountPath);
  }
}
