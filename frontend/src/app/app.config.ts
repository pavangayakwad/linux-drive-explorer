import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideRouter } from '@angular/router';
import Aura from '@primeuix/themes/aura';
import { definePreset, palette } from '@primeuix/themes';
import { providePrimeNG } from 'primeng/config';
import { ConfirmationService, MessageService } from 'primeng/api';

import { routes } from './app.routes';
import { THEME_HEX } from './core/services/theme.service';

// Default theme color (green). Signed-in users can switch to blue/violet at
// runtime via ThemeService, which restyles the primary palette in place.
const AppPreset = definePreset(Aura, {
  semantic: {
    primary: palette(THEME_HEX.green),
  },
});

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideAnimationsAsync(),
    providePrimeNG({
      theme: {
        preset: AppPreset,
        options: {
          darkModeSelector: '.app-dark',
        },
      },
    }),
    ConfirmationService,
    MessageService,
  ],
};
