import { AppWindow, AudioLines, ChevronRight, MonitorSpeaker, SlidersHorizontal } from 'lucide-react';
import { LANGUAGES, normalizeLanguage, useI18n } from '../i18n';
import type { SettingsModel } from '../types';
import { Modal } from './Dialogs';
import RuseLogo from './RuseLogo';

export default function WelcomeSplash({ settings, onChange, onStartSetup, onClose }: {
  settings: SettingsModel;
  onChange: (settings: SettingsModel) => void;
  onStartSetup: () => void;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const hideNextTime = !settings.showWelcomeScreen;

  return (
    <Modal title={t('Welcome to Ruse')} onClose={onClose} className="welcome-splash-modal">
      <div className="welcome-splash">
        <div className="welcome-splash-hero">
          <span className="welcome-splash-logo"><RuseLogo size={54} /></span>
          <div>
            <p className="wizard-kicker">{t('Sound routing, simplified')}</p>
            <h2>{t('Send sound anywhere.')}</h2>
            <p>{t('Ruse sends the sound from one app, the whole PC, or a microphone to any speaker, headphones or virtual device. It makes a copy, so the original app keeps working normally.')}</p>
          </div>
        </div>

        <div className="welcome-splash-cards">
          <div className="welcome-splash-card">
            <span className="welcome-splash-card-icon blue"><AppWindow size={18} /></span>
            <strong>{t('1. Pick a sound')}</strong>
            <small>{t('An application, everything Windows plays, or a microphone.')}</small>
          </div>
          <div className="welcome-splash-card">
            <span className="welcome-splash-card-icon amber"><SlidersHorizontal size={18} /></span>
            <strong>{t('2. Shape it')}</strong>
            <small>{t('Adjust volume, add effects, or record and transcribe the mix.')}</small>
          </div>
          <div className="welcome-splash-card">
            <span className="welcome-splash-card-icon green"><MonitorSpeaker size={18} /></span>
            <strong>{t('3. Send it anywhere')}</strong>
            <small>{t('Speakers, headphones, a virtual cable, or another app.')}</small>
          </div>
        </div>

        <div className="welcome-splash-note">
          <AudioLines size={18} />
          <div>
            <strong>{t('Start with a guided setup')}</strong>
            <p>{t('We will pick a sound, choose where it comes out, and test the connection together. You can skip it and explore the canvas on your own.')}</p>
          </div>
        </div>

        <div className="welcome-splash-footer">
          <label className="welcome-splash-language">
            <span>{t('Language')}</span>
            <select value={normalizeLanguage(settings.uiLanguage)} onChange={event => onChange({ ...settings, uiLanguage: event.target.value })}>
              {LANGUAGES.map(language => <option key={language.id} value={language.id}>{language.label}</option>)}
            </select>
          </label>
          <label className="welcome-splash-check">
            <input
              type="checkbox"
              checked={hideNextTime}
              onChange={event => onChange({ ...settings, showWelcomeScreen: !event.target.checked })}
            />
            <span>{t("Don't show this again")}</span>
          </label>
          <div className="welcome-splash-actions">
            <button className="tool-button" onClick={onClose}>{t('Explore on my own')}</button>
            <button className="primary-button" onClick={onStartSetup}>
              {t('Set up my first route')} <ChevronRight size={14} />
            </button>
          </div>
        </div>
      </div>
    </Modal>
  );
}
