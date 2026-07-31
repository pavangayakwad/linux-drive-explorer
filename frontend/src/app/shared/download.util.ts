import { apiClient } from '../core/http/api-client';

/**
 * Downloads a file through the authenticated API client rather than a plain `<a href>`/`window.location`
 * navigation - endpoints here require a bearer token that only apiClient attaches, so the response has to be
 * fetched as a blob first (same approach preview-dialog.component.ts already uses for previews).
 */
export async function downloadBlob(url: string, params: Record<string, string>, fallbackFileName: string): Promise<void> {
  const response = await apiClient.get<Blob>(url, { params, responseType: 'blob' });
  const fileName = extractFileName(response.headers['content-disposition']) ?? fallbackFileName;

  const objectUrl = URL.createObjectURL(response.data);
  try {
    const anchor = document.createElement('a');
    anchor.href = objectUrl;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    URL.revokeObjectURL(objectUrl);
  }
}

function extractFileName(contentDisposition?: string): string | null {
  if (!contentDisposition) {
    return null;
  }
  const match = /filename\*?=(?:UTF-8''|")?([^";]+)"?/i.exec(contentDisposition);
  return match ? decodeURIComponent(match[1]) : null;
}

export interface InsufficientSpaceInfo {
  requiredBytes: number;
  availableBytes: number;
}

/** True when a failed request was rejected because the zip staging volume doesn't have enough free space. */
export function isInsufficientSpaceError(error: unknown): boolean {
  return getInsufficientSpaceInfo(error) !== null;
}

export function getInsufficientSpaceInfo(error: unknown): InsufficientSpaceInfo | null {
  if (!error || typeof error !== 'object' || !('response' in error)) {
    return null;
  }
  const data = (error as { response?: { data?: { code?: string; requiredBytes?: number; availableBytes?: number } } }).response?.data;
  if (data?.code !== 'insufficient_space' || typeof data.requiredBytes !== 'number' || typeof data.availableBytes !== 'number') {
    return null;
  }
  return { requiredBytes: data.requiredBytes, availableBytes: data.availableBytes };
}
