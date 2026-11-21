export type ThemeId =
  | 'dark'
  | 'light'
  | 'blueprint'
  | 'studio74'
  | 'vesper';

export interface ThemeOption {
  id: ThemeId;
  label: string;
  chip: string;
  graphDotColor: string;
}

export const THEME_OPTIONS: readonly ThemeOption[] = [
  { id: 'dark', label: 'Dark', chip: '#242e3c', graphDotColor: '#34465A' },
  { id: 'light', label: 'Light', chip: '#f7f8fb', graphDotColor: '#C5CFDD' },
  { id: 'blueprint', label: 'Blueprint', chip: '#052548', graphDotColor: '#7CC4FF' },
  {
    id: 'studio74',
    label: "Studio '74",
    chip: 'linear-gradient(135deg, #2a1a0e 0 33%, #f0e4cc 33% 70%, #c97a18 70%)',
    graphDotColor: '#8A6A42',
  },
  {
    id: 'vesper',
    label: 'Vesper',
    chip: 'linear-gradient(135deg, #1b1d1f 0 33%, #cfd2d4 33% 70%, #e2662a 70%)',
    graphDotColor: '#6C7276',
  },
];

const THEME_BY_ID = new Map(THEME_OPTIONS.map(option => [option.id, option]));

export const THEME_IDS: readonly ThemeId[] = THEME_OPTIONS.map(option => option.id);

export function themeOption(id: string): ThemeOption {
  return THEME_BY_ID.get(id as ThemeId) ?? THEME_OPTIONS[0];
}

export function themeGraphDotColor(id: string): string {
  return themeOption(id).graphDotColor;
}
