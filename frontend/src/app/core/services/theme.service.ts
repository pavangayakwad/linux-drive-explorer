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

const THEME_STORAGE_KEY = 'fx_theme_color';

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

@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly current = signal<ThemeColor>('green');

  constructor() {
    // The server-persisted preference (see select()) only arrives after a successful login
    // round-trip, so the login page itself would otherwise always render the default green.
    // Restoring from localStorage here applies the last-known color immediately, before any
    // network call, on both the login page and the authenticated app.
    const stored = loadStoredThemeColor();
    if (stored) {
      this.apply(stored);
    }
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
}
