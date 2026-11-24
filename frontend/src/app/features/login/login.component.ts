import { isAxiosError } from 'axios';
import { CommonModule } from '@angular/common';
import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { MessageModule } from 'primeng/message';
import { PasswordModule } from 'primeng/password';
import { TooltipModule } from 'primeng/tooltip';
import { AppConfigService } from '../../core/services/app-config.service';
import { AuthService } from '../../core/services/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, InputTextModule, PasswordModule, MessageModule, TooltipModule],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss',
})
export class LoginComponent {
  username = '';
  password = '';
  readonly loading = signal(false);
  readonly errorMessage = signal<string | null>(null);

  constructor(
    private readonly authService: AuthService,
    readonly appConfig: AppConfigService,
    private readonly router: Router,
  ) {}

  async submit(): Promise<void> {
    if (!this.username || !this.password) {
      this.errorMessage.set('Enter your username and password.');
      return;
    }

    this.loading.set(true);
    this.errorMessage.set(null);

    try {
      await this.authService.login({ username: this.username, password: this.password });
      await this.router.navigateByUrl('/');
    } catch (error) {
      // 429 = account lockout or per-IP rate limit; surface the server's wait time instead of a misleading "wrong password".
      if (isAxiosError(error) && error.response?.status === 429) {
        this.errorMessage.set((error.response.data?.message as string | undefined) ?? 'Too many attempts. Please wait a minute and try again.');
      } else {
        this.errorMessage.set('Invalid username or password.');
      }
    } finally {
      this.loading.set(false);
    }
  }
}
