import { useState } from 'react';
import { LANGUAGES, normalizeLanguage, useI18n } from '../i18n';
import type { SettingsModel } from '../types';
import { Modal } from './Dialogs';
import RuseLogo from './RuseLogo';
import WelcomeConnectAnimation from './WelcomeConnectAnimation';
import WelcomeDragAnimation from './WelcomeDragAnimation';
import WelcomeExamplesAnimation from './WelcomeExamplesAnimation';
import WelcomeTimelineAnimation from './WelcomeTimelineAnimation';

export default function WelcomeWizard({ settings, onChange, onClose }: {
  settings: SettingsModel;
  onChange: (settings: SettingsModel) => void;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const [step, setStep] = useState<0 | 1 | 2 | 3>(0);
  const hideNextTime = !settings.showWelcomeScreen;

  return (
    <Modal title={t('Welcome to Ruse')} onClose={onClose} className="wizard-modal welcome-tour-modal">
      <div className="welcome-tour">
        {step === 0
          ? <WelcomeDragAnimation />
          : step === 1
            ? <WelcomeConnectAnimation />
            : step === 2
              ? <WelcomeTimelineAnimation />
              : <WelcomeExamplesAnimation />}

        <div className="welcome-tour-nav">
          {step === 0
            ? <button className="tool-button" onClick={onClose}>{t('Explore on my own')}</button>
            : <button className="tool-button" onClick={() => setStep((step - 1) as 0 | 1 | 2)}>{t('Back')}</button>}
          <button
            className="primary-button"
            onClick={step === 3 ? onClose : () => setStep((step + 1) as 1 | 2 | 3)}
          >
            {step === 3 ? t('Done') : t('Next')}
          </button>
        </div>

        <div className="welcome-tour-copy">
          <div className="welcome-tour-heading">
            <span className="welcome-tour-logo"><RuseLogo size={34} /></span>
            <h2>{step === 0
              ? t('Send sound anywhere.')
              : step === 1
                ? t('Join nodes with cables')
                : step === 2
                  ? t('Build with the timeline')
                  : t('See what you can build')}</h2>
          </div>
          <div className="welcome-tour-lead">
            <p>
              {step === 0
                ? t('Drag apps, devices, effects and recorders from the left side onto the canvas.')
                : step === 1
                  ? t('Drag from the OUT dot on a node to the IN dot on another node. The cable carries the sound.')
                  : step === 2
                    ? t('Open the Timeline, add a track, drop samples or recordings, then press Space to play. Drag an effect onto a cable to build an effect chain.')
                    : t('Effects, transcription, translation, recording and network audio, all on the canvas.')}
            </p>
            {settings.appVersion && <span className="welcome-tour-version">{settings.appVersion}</span>}
          </div>
        </div>

        <div className="welcome-tour-footer">
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
        </div>
      </div>
    </Modal>
  );
}
