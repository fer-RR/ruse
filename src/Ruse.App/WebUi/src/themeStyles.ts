import type { ThemeId } from './themes';

type ManagedTheme = Exclude<ThemeId, 'dark'>;

const loaders: Record<ManagedTheme, () => Promise<unknown>> = {
  light: () => import('./themes/light.css'),
  blueprint: () => import('./themes/blueprint.css'),
  studio74: () => import('./themes/studio74.css'),
  vesper: () => import('./themes/vesper.css'),
};

const loaded = new Set<ThemeId>(['dark']);

export async function ensureThemeStyles(theme: ThemeId): Promise<void> {
  if (theme === 'dark' || loaded.has(theme)) {
    return;
  }

  await loaders[theme]();
  loaded.add(theme);
}
