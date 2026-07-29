export interface Permissions {
  owner: string;
  group: string;
  octalMode: string;
  symbolicMode: string;
  supported: boolean;
}

export interface Principal {
  name: string;
  id: string;
}

export interface PrincipalsResponse {
  users: Principal[];
  groups: Principal[];
}
