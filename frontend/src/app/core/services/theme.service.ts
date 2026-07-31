import { Injectable, signal } from '@angular/core';
import { palette, updatePrimaryPalette } from '@primeuix/themes';
import type { PaletteDesignToken } from '@primeuix/themes/types';
import { apiClient } from '../http/api-client';
import { ThemeColor } from '../models/auth.model';

export const THEME_HEX: Record<ThemeColor, string> = {
  green: '#01754F',
  blue: '#0070CB',
  violet: '#472270',
};

export const THEME_COLORS: ThemeColor[] = ['green', 'blue', 'violet'];

export type ThemeMode = 'light' | 'dark';
export const THEME_MODES: ThemeMode[] = ['light', 'dark'];

const THEME_STORAGE_KEY = 'fx_theme_color';
const THEME_MODE_STORAGE_KEY = 'fx_theme_mode';

// Must match providePrimeNG's theme.options.darkModeSelector in app.config.ts.
const DARK_MODE_CLASS = 'app-dark';

function loadStoredThemeColor(): ThemeColor | null {
  try {
    const raw = localStorage.getItem(THEME_STORAGE_KEY);
    return THEME_COLORS.includes(raw as ThemeColor) ? (raw as ThemeColor) : null;
  } catch {
    // Best-effort only - a full/unavailable localStorage just means no early theme to restore.
    return null;
  }
}

function saveThemeColor(color: ThemeColor): void {
  try {
    localStorage.setItem(THEME_STORAGE_KEY, color);
  } catch {
    // Best-effort only.
  }
}

function loadStoredThemeMode(): ThemeMode | null {
  try {
    const raw = localStorage.getItem(THEME_MODE_STORAGE_KEY);
    return THEME_MODES.includes(raw as ThemeMode) ? (raw as ThemeMode) : null;
  } catch {
    return null;
  }
}

function saveThemeMode(mode: ThemeMode): void {
  try {
    localStorage.setItem(THEME_MODE_STORAGE_KEY, mode);
  } catch {
    // Best-effort only.
  }
}

@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly current = signal<ThemeColor>('green');
  readonly mode = signal<ThemeMode>('light');

  constructor() {
    // The server-persisted preference (see select()) only arrives after a successful login
    // round-trip, so the login page itself would otherwise always render the default green.
    // Restoring from localStorage here applies the last-known color immediately, before any
    // network call, on both the login page and the authenticated app.
    const stored = loadStoredThemeColor();
    if (stored) {
      this.apply(stored);
    }

    // Dark/light mode is local-only (no server round-trip), so it's always safe to restore
    // straight from localStorage here, before the login form or app paints.
    this.applyMode(loadStoredThemeMode() ?? 'light');
  }

  /** Applies a theme color to the running app and caches it locally so it survives a reload/logout. */
  apply(color: ThemeColor): void {
    updatePrimaryPalette(palette(THEME_HEX[color]) as PaletteDesignToken);
    this.current.set(color);
    saveThemeColor(color);
  }

  /** Applies the color and saves it as the signed-in user's preference so it's restored on their next login. */
  async select(color: ThemeColor): Promise<void> {
    this.apply(color);
    await apiClient.put('/auth/theme', { themeColor: color });
  }

  /** Switches between light/dark mode and caches the choice so it survives a reload. */
  applyMode(mode: ThemeMode): void {
    document.documentElement.classList.toggle(DARK_MODE_CLASS, mode === 'dark');
    this.mode.set(mode);
    saveThemeMode(mode);
  }

  toggleMode(): void {
    this.applyMode(this.mode() === 'dark' ? 'light' : 'dark');
  }
}
