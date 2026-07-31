export type UploadItemStatus = 'pending' | 'uploading' | 'done' | 'error' | 'cancelled';

export interface UploadItem {
  id: string;
  name: string;
  relativePath: string;
  destinationPath: string;
  status: UploadItemStatus;
  loadedBytes: number;
  totalBytes: number;
  errorMessage?: string;
}
