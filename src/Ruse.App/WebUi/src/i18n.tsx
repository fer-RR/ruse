import { createContext, useContext, useMemo } from 'react';
import type { ReactNode } from 'react';
import { de } from './locales/de';
import { es } from './locales/es';
import { fr } from './locales/fr';
import { it } from './locales/it';
import { ja } from './locales/ja';
import { ko } from './locales/ko';
import { ptBR } from './locales/pt-BR';
import { zhCN } from './locales/zh-CN';

export type Language = 'en' | 'pt-BR' | 'es' | 'fr' | 'de' | 'it' | 'ja' | 'ko' | 'zh-CN';

export const LANGUAGES: ReadonlyArray<{ id: Language; label: string }> = [
  { id: 'en', label: 'English' },
  { id: 'pt-BR', label: 'Português (Brasil)' },
  { id: 'es', label: 'Español' },
  { id: 'fr', label: 'Français' },
  { id: 'de', label: 'Deutsch' },
  { id: 'it', label: 'Italiano' },
  { id: 'ja', label: '日本語' },
  { id: 'ko', label: '한국어' },
  { id: 'zh-CN', label: '中文（简体）' },
];

const DICTIONARIES: Partial<Record<Language, Record<string, string>>> = {
  'pt-BR': ptBR,
  es,
  fr,
  de,
  it,
  ja,
  ko,
  'zh-CN': zhCN,
};

const LANGUAGE_IDS = new Set(LANGUAGES.map(language => language.id));

export function normalizeLanguage(value?: string): Language {
  return value && LANGUAGE_IDS.has(value as Language) ? value as Language : 'en';
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
