export type FileOperationType = 'Copy' | 'Move' | 'Delete' | 'PurgeTrash';

export type FileOperationStatus = 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Cancelled';

export interface Job {
  id: string;
  type: FileOperationType;
  sources: string[];
  destination: string | null;
  status: FileOperationStatus;
  totalItems: number;
  processedItems: number;
  totalBytes: number;
  processedBytes: number;
  currentItem: string | null;
  errorMessage: string | null;
  permanentlyDeletedNames: string[];
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
}

export interface TrashItem {
  id: string;
  originalPath: string;
  name: string;
  isDirectory: boolean;
  sizeBytes: number | null;
  deletedAt: string;
}
