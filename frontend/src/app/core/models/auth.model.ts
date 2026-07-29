export type ThemeColor = 'green' | 'blue' | 'violet';

export interface LoginRequest {
  username: string;
  password: string;
}

export interface AuthResponse {
  accessToken: string;
  accessTokenExpiresAt: string;
  refreshToken: string;
  username: string;
  role: string;
  themeColor: ThemeColor;
}
