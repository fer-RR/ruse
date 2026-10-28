export interface TranslationTarget {
  code: string;
  label: string;
  needsModel: boolean;
}

export const TRANSLATION_TARGETS: TranslationTarget[] = [
  { code: 'en', label: 'English', needsModel: false },
  { code: 'pt-BR', label: 'Portuguese (Brazil)', needsModel: true },
  { code: 'es', label: 'Spanish', needsModel: true },
  { code: 'fr', label: 'French', needsModel: true },
  { code: 'de', label: 'German', needsModel: true },
  { code: 'it', label: 'Italian', needsModel: true },
  { code: 'ja', label: 'Japanese', needsModel: true },
  { code: 'ko', label: 'Korean', needsModel: true },
  { code: 'zh-CN', label: 'Chinese (Simplified)', needsModel: true },
];

export function translationTarget(code?: string): TranslationTarget {
  return TRANSLATION_TARGETS.find(target => target.code === code) ?? TRANSLATION_TARGETS[0];
}

export function languageLabel(code?: string): string {
  if (!code) return '';
  const normalized = code.toLowerCase();
  const match = TRANSLATION_TARGETS.find(target =>
    target.code.toLowerCase() === normalized ||
    target.code.split('-')[0].toLowerCase() === normalized);
  return match?.label ?? code;
}
