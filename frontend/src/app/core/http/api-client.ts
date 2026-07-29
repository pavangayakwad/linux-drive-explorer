import axios, { AxiosError, InternalAxiosRequestConfig } from 'axios';
import { tokenStore } from './token-store';

const PUBLIC_PATHS = ['/auth/login', '/auth/refresh'];

function isPublicPath(url: string | undefined): boolean {
  return !!url && PUBLIC_PATHS.some((path) => url.includes(path));
}

export const apiClient = axios.create({ baseURL: '/api' });

apiClient.interceptors.request.use((config) => {
  if (!isPublicPath(config.url) && tokenStore.accessToken) {
    config.headers.set('Authorization', `Bearer ${tokenStore.accessToken}`);
  }
  return config;
});

apiClient.interceptors.response.use(
  (response) => response,
  async (error: AxiosError) => {
    const originalRequest = error.config as (InternalAxiosRequestConfig & { _retried?: boolean }) | undefined;

    if (
      error.response?.status === 401 &&
      originalRequest &&
      !originalRequest._retried &&
      !isPublicPath(originalRequest.url)
    ) {
      originalRequest._retried = true;
      const refreshed = await tokenStore.refresh();
      if (refreshed) {
        originalRequest.headers.set('Authorization', `Bearer ${tokenStore.accessToken}`);
        return apiClient(originalRequest);
      }
    }

    return Promise.reject(error);
  },
);
