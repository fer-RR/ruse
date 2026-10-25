import type { CatalogItem } from './types';

export function catalogKey(item: CatalogItem): string {
  return item.kind === 'app' ? `app:${item.name}` : `${item.kind}:${item.id}`;
}
