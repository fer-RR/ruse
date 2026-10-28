import { createContext, useContext, useMemo } from 'react';
import type { ReactNode } from 'react';
import { ptBR } from './locales/pt-BR';

export type Language = 'en' | 'pt-BR';

export const LANGUAGES: ReadonlyArray<{ id: Language; label: string }> = [
  { id: 'en', label: 'English' },
  { id: 'pt-BR', label: 'Português (Brasil)' },
];

const DICTIONARIES: Partial<Record<Language, Record<string, string>>> = {
  'pt-BR': ptBR,
};

export function normalizeLanguage(value?: string): Language {
  return value === 'pt-BR' ? 'pt-BR' : 'en';
}

export function translate(language: Language, text: string, vars?: Record<string, string | number>): string {
  let result = DICTIONARIES[language]?.[text] ?? text;
  if (vars) {
    for (const [name, value] of Object.entries(vars)) {
      result = result.split(`{${name}}`).join(String(value));
    }
  }
  return result;
}

interface I18nValue {
  language: Language;
  t: (text: string, vars?: Record<string, string | number>) => string;
}

const I18nContext = createContext<I18nValue>({
  language: 'en',
  t: (text, vars) => translate('en', text, vars),
});

export function I18nProvider({ language, children }: { language: Language; children: ReactNode }) {
  const value = useMemo<I18nValue>(() => ({
    language,
    t: (text, vars) => translate(language, text, vars),
  }), [language]);

  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

export function useI18n(): I18nValue {
  return useContext(I18nContext);
}
