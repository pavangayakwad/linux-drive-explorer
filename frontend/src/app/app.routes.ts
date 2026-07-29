import { Routes } from '@angular/router';
import { authGuard } from './core/guards/auth.guard';

export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () => import('./features/login/login.component').then((m) => m.LoginComponent),
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./features/shell/shell.component').then((m) => m.ShellComponent),
    children: [
      {
        path: '',
        loadComponent: () =>
          import('./features/explorer/explorer.component').then((m) => m.ExplorerComponent),
      },
      {
        path: 'tasks',
        loadComponent: () => import('./features/tasks/tasks.component').then((m) => m.TasksComponent),
      },
      {
        path: 'trash',
        loadComponent: () => import('./features/trash/trash.component').then((m) => m.TrashComponent),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
