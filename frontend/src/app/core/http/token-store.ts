type RefreshHandler = () => Promise<boolean>;

/**
 * Plain (non-Angular-injectable) holder for the current access token, shared between
 * AuthService and the axios interceptors in api-client.ts. Keeping it outside Angular's DI
 * avoids a circular dependency between the http client module and AuthService.
 */
class TokenStore {
  accessToken: string | null = null;
  private refreshHandler: RefreshHandler | null = null;

  setRefreshHandler(handler: RefreshHandler): void {
    this.refreshHandler = handler;
  }

  refresh(): Promise<boolean> {
    return this.refreshHandler ? this.refreshHandler() : Promise.resolve(false);
  }
}

export const tokenStore = new TokenStore();
