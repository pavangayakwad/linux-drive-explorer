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

@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly current = signal<ThemeColor>('green');

  /** Applies a theme color to the running app without persisting it - used when a fresh auth response arrives. */
  apply(color: ThemeColor): void {
    updatePrimaryPalette(palette(THEME_HEX[color]) as PaletteDesignToken);
    this.current.set(color);
  }

  /** Applies the color and saves it as the signed-in user's preference so it's restored on their next login. */
  async select(color: ThemeColor): Promise<void> {
    this.apply(color);
    await apiClient.put('/auth/theme', { themeColor: color });
  }
}
