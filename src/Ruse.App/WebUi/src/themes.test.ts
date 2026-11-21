import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { THEME_OPTIONS, themeGraphDotColor, themeOption } from './themes';
import { ensureThemeStyles } from './themeStyles';

const baseCss = readFileSync(new URL('./styles.css', import.meta.url), 'utf8');
const themeCss = Object.fromEntries(
  THEME_OPTIONS.map(theme => [
    theme.id,
    readFileSync(new URL(`./themes/${theme.id}.css`, import.meta.url), 'utf8'),
  ]),
) as Record<string, string>;
const persistence = readFileSync(new URL('../../WebShellController.Persistence.cs', import.meta.url), 'utf8');
const controller = readFileSync(new URL('../../WebShellController.cs', import.meta.url), 'utf8');
const managedThemes = THEME_OPTIONS.filter(theme => theme.id !== 'dark');

describe('theme registry parity', () => {
  it('has unique ids', () => {
    expect(new Set(THEME_OPTIONS.map(theme => theme.id)).size).toBe(THEME_OPTIONS.length);
  });

  it('provides a chip and graph colour for every theme', () => {
    for (const theme of THEME_OPTIONS) {
      expect(theme.chip.length).toBeGreaterThan(0);
      expect(theme.graphDotColor).toMatch(/^#[0-9a-f]{6}$/i);
    }
  });

  it('keeps theme selectors out of the base stylesheet', () => {
    expect(baseCss).not.toContain(':root[data-theme');
  });

  it('ships the default dark tokens with the base styles', () => {
    expect(themeCss.dark).toContain(':root {');
    expect(themeCss.dark).toContain('--canvas');
  });

  it.each(managedThemes)('$id has its own CSS file and settings whitelist', theme => {
    expect(themeCss[theme.id]).toContain(`[data-theme="${theme.id}"]`);
    expect(persistence).toContain(`"${theme.id}"`);
    expect(controller).toContain(`"${theme.id}"`);
  });

  it('keeps the logotype typeface fixed and removes the dot', () => {
    expect(baseCss).toMatch(/\.brand-plate strong[^}]*Bahnschrift/);
    for (const theme of THEME_OPTIONS) {
      expect(themeCss[theme.id]).not.toMatch(/\.brand-plate strong::before/);
    }
  });

  it('keeps repeating patterns off the graph canvas', () => {
    for (const theme of THEME_OPTIONS) {
      expect(themeCss[theme.id]).not.toMatch(/\.graph-area[^{]*\{[^}]*repeating-linear-gradient/);
    }
  });

  it('falls back to the default theme', () => {
    expect(themeOption('not-a-theme').id).toBe('dark');
    expect(themeGraphDotColor('vesper')).toBe('#6C7276');
  });

  it('does not expose the removed Receiver theme', () => {
    expect(THEME_OPTIONS.some(theme => (theme.id as string) === 'receiver')).toBe(false);
    expect(themeOption('receiver').id).toBe('dark');
    expect(persistence).toContain('"receiver" => "dark"');
    expect(controller).toContain('"receiver" => "dark"');
  });

  it('loads every managed theme stylesheet', async () => {
    for (const theme of managedThemes) {
      await expect(ensureThemeStyles(theme.id)).resolves.toBeUndefined();
    }
  });
});
