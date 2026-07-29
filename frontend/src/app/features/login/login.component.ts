import { CommonModule } from '@angular/common';
import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { MessageModule } from 'primeng/message';
import { PasswordModule } from 'primeng/password';
import { AuthService } from '../../core/services/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, InputTextModule, PasswordModule, MessageModule],
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
    } catch {
      this.errorMessage.set('Invalid username or password.');
    } finally {
      this.loading.set(false);
    }
  }
}
