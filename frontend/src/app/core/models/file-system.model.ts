export interface FileEntry {
  name: string;
  path: string;
  isDirectory: boolean;
  sizeBytes: number | null;
  createdAt: string;
  modifiedAt: string;
  extension: string | null;
  typeLabel: string;
  unixPermissions: string | null;
  isHidden: boolean;
}

export interface DirectoryListing {
  path: string;
  parentPath: string | null;
  entries: FileEntry[];
}

export interface DriveSummary {
  name: string;
  mountPath: string;
  driveFormat: string;
  totalBytes: number;
  freeBytes: number;
  isRemovable: boolean;
}

export type DirectorySizeStatus = 'Running' | 'Completed' | 'Cancelled' | 'Failed';

export interface DirectorySizeJob {
  jobId: string;
  path: string;
  status: DirectorySizeStatus;
  bytes: number;
}
