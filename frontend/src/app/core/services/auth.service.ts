import { Injectable, signal } from '@angular/core';
import { Router } from '@angular/router';
import { apiClient } from '../http/api-client';
import { tokenStore } from '../http/token-store';
import { AuthResponse, LoginRequest } from '../models/auth.model';
import { ThemeService } from './theme.service';

const REFRESH_TOKEN_KEY = 'fx_refresh_token';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly authenticatedSignal = signal(false);
  private readonly usernameSignal = signal<string | null>(null);

  readonly isAuthenticated = this.authenticatedSignal.asReadonly();
  readonly username = this.usernameSignal.asReadonly();

  private refreshInFlight: Promise<boolean> | null = null;

  constructor(
    private readonly router: Router,
    private readonly themeService: ThemeService,
  ) {
    tokenStore.setRefreshHandler(() => this.refreshSession());
  }

  get accessToken(): string | null {
    return tokenStore.accessToken;
  }

  async login(request: LoginRequest): Promise<void> {
    const { data } = await apiClient.post<AuthResponse>('/auth/login', request);
    this.applyAuthResponse(data);
  }

  async tryRestoreSession(): Promise<boolean> {
    const refreshToken = localStorage.getItem(REFRESH_TOKEN_KEY);
    if (!refreshToken) {
      return false;
    }
    return this.refreshSession();
  }

  async refreshSession(): Promise<boolean> {
    if (this.refreshInFlight) {
      return this.refreshInFlight;
    }

    this.refreshInFlight = this.doRefresh();
    const result = await this.refreshInFlight;
    this.refreshInFlight = null;
    return result;
  }

  private async doRefresh(): Promise<boolean> {
    const refreshToken = localStorage.getItem(REFRESH_TOKEN_KEY);
    if (!refreshToken) {
      return false;
    }

    try {
      const { data } = await apiClient.post<AuthResponse>('/auth/refresh', { refreshToken });
      this.applyAuthResponse(data);
      return true;
    } catch {
      this.clearSession();
      return false;
    }
  }

  async logout(): Promise<void> {
    const refreshToken = localStorage.getItem(REFRESH_TOKEN_KEY);
    this.clearSession();
    if (refreshToken) {
      try {
        await apiClient.post('/auth/logout', { refreshToken });
      } catch {
        // best-effort - the local session is already cleared
      }
    }
    await this.router.navigateByUrl('/login');
  }

  /** Change the current user's password. The backend revokes the refresh token on success, so we sign out locally. */
  async changePassword(newPassword: string): Promise<void> {
    await apiClient.post('/auth/change-password', { newPassword });
    this.clearSession();
    await this.router.navigateByUrl('/login');
  }

  private applyAuthResponse(response: AuthResponse): void {
    tokenStore.accessToken = response.accessToken;
    this.usernameSignal.set(response.username);
    this.authenticatedSignal.set(true);
    localStorage.setItem(REFRESH_TOKEN_KEY, response.refreshToken);
    this.themeService.apply(response.themeColor);
  }

  private clearSession(): void {
    tokenStore.accessToken = null;
    this.usernameSignal.set(null);
    this.authenticatedSignal.set(false);
    localStorage.removeItem(REFRESH_TOKEN_KEY);
  }
}
