import type { ComponentType } from 'react';
import { Modal } from './Dialogs';
import { useI18n } from '../i18n';

export type ExplainerId = 'mixer' | 'sidechain' | 'vst3' | 'native';

const COPY: Record<ExplainerId, { title: string; lead: string }> = {
  mixer: {
    title: 'Mixer',
    lead: 'Each source and output has its own fader. Master volume is at the bottom right of the window.',
  },
  sidechain: {
    title: 'Sidechain ducker',
    lead: 'Ducks this route when a separate key source passes the threshold.',
  },
  vst3: {
    title: 'VST3 plugins',
    lead: 'Insert a VST3 effect from the library, then open its editor to shape the sound. Ruse can isolate each plugin in a child process.',
  },
  native: {
    title: 'Native passthrough',
    lead: 'Native passthrough sends a source straight to the device without the graph. Use it when the source already matches the output.',
  },
};

function MixerExplainer() {
  const { t } = useI18n();
  const strips = [
    { name: 'Music', x: 76, meter: 'explainer-mixer-meter-a', fader: 'explainer-mixer-fader-a', knobY: 120, level: '0.0' },
    { name: 'Voice', x: 220, meter: 'explainer-mixer-meter-b', fader: 'explainer-mixer-fader-b', knobY: 132, level: '-4.5' },
    { name: 'Game', x: 364, meter: 'explainer-mixer-meter-c', fader: 'explainer-mixer-fader-c', knobY: 112, level: '+1.5' },
    { name: 'Master', x: 548, meter: 'explainer-mixer-meter-d', fader: 'explainer-mixer-fader-d', knobY: 126, level: '-1.0' },
  ];
  return (
    <div className="welcome-drag explainer-visual">
      <svg className="welcome-drag-canvas explainer-svg" viewBox="0 0 720 280" role="img" aria-label={t(COPY.mixer.lead)}>
        <rect className="welcome-drag-stage" x="0.5" y="0.5" width="719" height="279" rx="16" />
        <rect className="welcome-drag-panel" x="24" y="20" width="672" height="208" rx="14" />
        <text className="welcome-drag-label" x="42" y="41">{t('Mixer').toUpperCase()}</text>
        <text className="explainer-mixer-hint" x="624" y="41" textAnchor="end">Master</text>

        {strips.map((strip, index) => (
          <g key={strip.name}>
            <rect className={`welcome-drag-card ${index === strips.length - 1 ? 'explainer-mixer-master' : ''}`} x={strip.x} y="56" width="108" height="154" rx="9" />
            <text className="welcome-timeline-node-title" x={strip.x + 12} y="76">{strip.name}</text>
            <rect className="explainer-mixer-meter-track" x={strip.x + 14} y="90" width="10" height="96" rx="3" />
            <rect className={`explainer-mixer-meter ${strip.meter}`} x={strip.x + 15} y="90" width="8" height="96" rx="2" />
            <rect className="explainer-mixer-fader-track" x={strip.x + 44} y="90" width="4" height="96" rx="2" />
            <rect className={`explainer-mixer-fader ${strip.fader}`} x={strip.x + 37} y={strip.knobY} width="18" height="8" rx="3" />
            <text className="explainer-mixer-level" x={strip.x + 74} y="142" textAnchor="middle">{strip.level}</text>
            <circle className="explainer-mixer-button" cx={strip.x + 30} cy="204" r="6" />
            <text className="explainer-mixer-button-text" x={strip.x + 30} y="207" textAnchor="middle">M</text>
            <circle className="explainer-mixer-button" cx={strip.x + 48} cy="204" r="6" />
            <text className="explainer-mixer-button-text" x={strip.x + 48} y="207" textAnchor="middle">S</text>
            <rect className="explainer-mixer-send" x={strip.x + 64} y="198" width="32" height="12" rx="3" />
          </g>
        ))}

        <path className="welcome-timeline-cable" d="M184 132 C 196 132, 208 132, 220 132" />
        <path className="welcome-timeline-cable" d="M328 132 C 340 132, 352 132, 364 132" />
        <path className="welcome-timeline-cable" d="M472 132 C 500 132, 520 132, 548 132" />
        <circle className="explainer-mixer-master-glow" cx="602" cy="132" r="26" />
        <text className="explainer-mixer-caption" x="360" y="252" textAnchor="middle">Faders  ·  M / S  ·  Master</text>
      </svg>
    </div>
  );
}

function SidechainExplainer() {
  const { t } = useI18n();
  return (
    <div className="welcome-drag explainer-visual">
      <svg className="welcome-drag-canvas explainer-svg" viewBox="0 0 720 280" role="img" aria-label={t(COPY.sidechain.lead)}>
        <rect className="welcome-drag-stage" x="0.5" y="0.5" width="719" height="279" rx="16" />
        <rect className="welcome-drag-panel" x="24" y="20" width="672" height="208" rx="14" />
        <text className="welcome-drag-label" x="42" y="41">SIDECHAIN DUCKER</text>

        <g className="welcome-timeline-node">
          <rect x="62" y="76" width="140" height="62" rx="9" />
          <circle className="welcome-timeline-glyph-bubble" cx="82" cy="97" r="10" />
          <path className="welcome-timeline-glyph" d="M78 94v6M83 94v6M86 95v4a5 5 0 0 1-10 0v-4" />
          <text className="welcome-timeline-node-title" x="100" y="101">Music</text>
          <text className="explainer-sc-subtitle" x="100" y="116">Main route</text>
          <circle className="welcome-timeline-port" cx="202" cy="107" r="4" />
        </g>

        <g className="welcome-timeline-node explainer-sc-ducker">
          <rect x="306" y="76" width="150" height="62" rx="9" />
          <text className="welcome-timeline-node-title" x="320" y="99">Ducker</text>
          <text className="explainer-sc-subtitle" x="320" y="115">Key input</text>
          <circle className="welcome-timeline-port welcome-timeline-port-in" cx="306" cy="107" r="4" />
          <circle className="welcome-timeline-port" cx="456" cy="107" r="4" />
          <circle className="explainer-sc-key-port" cx="381" cy="138" r="5" />
          <text className="explainer-sc-key-label" x="381" y="132" textAnchor="middle">KEY</text>
        </g>

        <g className="welcome-timeline-node">
          <rect x="552" y="76" width="108" height="62" rx="9" />
          <circle className="welcome-timeline-glyph-bubble" cx="570" cy="97" r="10" />
          <path className="welcome-timeline-glyph" d="M568 94l-4 3h-2v4h2l4 3v-10ZM575 95a5 5 0 0 1 0 8" />
          <text className="welcome-timeline-node-title" x="586" y="101">Speakers</text>
          <circle className="welcome-timeline-port welcome-timeline-port-in" cx="552" cy="107" r="4" />
        </g>

        <g className="welcome-timeline-node">
          <rect x="62" y="176" width="140" height="52" rx="9" />
          <circle className="welcome-timeline-glyph-bubble" cx="82" cy="195" r="10" />
          <path className="welcome-timeline-glyph" d="M79 193v7M84 193v7M87 194v4a5 5 0 0 1-10 0v-4" />
          <text className="welcome-timeline-node-title" x="100" y="199">Voice</text>
          <text className="explainer-sc-subtitle" x="100" y="214">Key source</text>
          <circle className="welcome-timeline-port" cx="202" cy="202" r="4" />
        </g>

        <path className="welcome-timeline-cable" d="M202 107 C 240 107, 270 107, 306 107" />
        <path className="welcome-timeline-cable" d="M456 107 C 492 107, 520 107, 552 107" />
        <path className="explainer-sc-key-cable" d="M202 202 C 248 202, 270 138, 381 138" pathLength={1} />
        <path className="explainer-sc-key-flow" d="M202 202 C 248 202, 270 138, 381 138" pathLength={1} />
        <circle className="explainer-sc-key-glow" cx="381" cy="138" r="18" />

        <rect className="explainer-sc-reduction-track" x="320" y="150" width="76" height="7" rx="3" />
        <rect className="explainer-sc-reduction" x="320" y="150" width="76" height="7" rx="3" />
        <text className="explainer-sc-reduction-label" x="320" y="172">Gain reduction</text>
        <text className="explainer-mixer-caption" x="360" y="252" textAnchor="middle">Connect a key source to the KEY input</text>
      </svg>
    </div>
  );
}

function Vst3Explainer() {
  const { t } = useI18n();
  return (
    <div className="welcome-drag explainer-visual">
      <svg className="welcome-drag-canvas explainer-svg" viewBox="0 0 720 280" role="img" aria-label={t(COPY.vst3.lead)}>
        <rect className="welcome-drag-stage" x="0.5" y="0.5" width="719" height="279" rx="16" />
        <rect className="welcome-drag-panel" x="24" y="20" width="672" height="208" rx="14" />
        <text className="welcome-drag-label" x="42" y="41">VST3 PLUGINS</text>

        <g>
          <rect className="welcome-drag-card" x="46" y="66" width="144" height="138" rx="9" />
          <text className="welcome-timeline-node-title" x="60" y="88">Library</text>
          <rect className="explainer-vst3-library-row" x="60" y="102" width="116" height="30" rx="6" />
          <text className="explainer-vst3-small" x="72" y="121">Plugins</text>
          <rect className="explainer-vst3-library-row" x="60" y="142" width="116" height="22" rx="5" />
          <text className="explainer-vst3-small" x="72" y="157">Effects</text>
        </g>

        <g className="welcome-timeline-node">
          <rect x="258" y="104" width="156" height="66" rx="9" />
          <circle className="welcome-timeline-glyph-bubble" cx="278" cy="126" r="10" />
          <path className="welcome-timeline-glyph" d="M274 124h8v7h-8zM277 124v-3h2v3M277 131v2M280 131v2" />
          <text className="welcome-timeline-node-title" x="296" y="130">VST3 effect</text>
          <text className="explainer-sc-subtitle" x="296" y="146">Inserted</text>
          <circle className="welcome-timeline-port welcome-timeline-port-in" cx="258" cy="137" r="4" />
          <circle className="welcome-timeline-port" cx="414" cy="137" r="4" />
        </g>

        <g className="explainer-vst3-ghost">
          <rect x="60" y="102" width="116" height="30" rx="6" />
          <text x="72" y="121">Plugins</text>
        </g>

        <path className="welcome-timeline-cable" d="M414 137 C 426 137, 434 137, 446 137" />
        <path className="explainer-vst3-editor-cable" d="M414 137 C 426 137, 434 137, 446 137" pathLength={1} />

        <g className="explainer-vst3-editor">
          <rect x="446" y="54" width="220" height="174" rx="10" />
          <rect className="explainer-vst3-editor-bar" x="446" y="54" width="220" height="24" rx="10" />
          <text className="explainer-vst3-editor-title" x="460" y="70">Plugin editor</text>
          <circle className="explainer-vst3-dot" cx="646" cy="66" r="4" />
          <text className="explainer-vst3-param" x="462" y="101">Drive</text>
          <rect className="explainer-vst3-slider" x="462" y="108" width="184" height="5" rx="2" />
          <rect className="explainer-vst3-knob explainer-vst3-knob-a" x="512" y="104" width="12" height="13" rx="3" />
          <text className="explainer-vst3-param" x="462" y="137">Tone</text>
          <rect className="explainer-vst3-slider" x="462" y="144" width="184" height="5" rx="2" />
          <rect className="explainer-vst3-knob explainer-vst3-knob-b" x="560" y="140" width="12" height="13" rx="3" />
          <text className="explainer-vst3-param" x="462" y="173">Mix</text>
          <rect className="explainer-vst3-slider" x="462" y="180" width="184" height="5" rx="2" />
          <rect className="explainer-vst3-knob explainer-vst3-knob-c" x="600" y="176" width="12" height="13" rx="3" />
          <rect className="explainer-vst3-sync" x="592" y="202" width="58" height="16" rx="8" />
          <text className="explainer-vst3-sync-text" x="621" y="213" textAnchor="middle">Synced</text>
        </g>

        <text className="explainer-mixer-caption" x="360" y="252" textAnchor="middle">Insert  ·  Open editor  ·  Automate</text>
      </svg>
    </div>
  );
}

function NativeExplainer() {
  const { t } = useI18n();
  return (
    <div className="welcome-drag explainer-visual">
      <svg className="welcome-drag-canvas explainer-svg" viewBox="0 0 720 280" role="img" aria-label={t(COPY.native.lead)}>
        <rect className="welcome-drag-stage" x="0.5" y="0.5" width="719" height="279" rx="16" />
        <rect className="welcome-drag-panel" x="24" y="20" width="672" height="208" rx="14" />
        <text className="welcome-drag-label" x="42" y="41">NATIVE PASSTHROUGH</text>

        <g className="welcome-timeline-node">
          <rect x="58" y="76" width="130" height="60" rx="9" />
          <circle className="welcome-timeline-glyph-bubble" cx="78" cy="96" r="10" />
          <path className="welcome-timeline-glyph" d="M76 92v8M80 92v8M83 94v4a5 5 0 0 1-10 0v-4" />
          <text className="welcome-timeline-node-title" x="96" y="100">Source</text>
          <text className="explainer-sc-subtitle" x="96" y="116">48 kHz</text>
          <circle className="welcome-timeline-port" cx="188" cy="106" r="4" />
        </g>

        <g className="welcome-timeline-node explainer-native-chain">
          <rect x="266" y="76" width="142" height="60" rx="9" />
          <text className="welcome-timeline-node-title" x="280" y="100">Effects</text>
          <text className="explainer-sc-subtitle" x="280" y="116">Graph processing</text>
          <circle className="welcome-timeline-port welcome-timeline-port-in" cx="266" cy="106" r="4" />
          <circle className="welcome-timeline-port" cx="408" cy="106" r="4" />
        </g>

        <g className="welcome-timeline-node">
          <rect x="540" y="76" width="120" height="60" rx="9" />
          <circle className="welcome-timeline-glyph-bubble" cx="558" cy="96" r="10" />
          <path className="welcome-timeline-glyph" d="M556 93l-4 3h-2v4h2l4 3v-10ZM563 94a5 5 0 0 1 0 8" />
          <text className="welcome-timeline-node-title" x="574" y="100">Output</text>
          <text className="explainer-sc-subtitle" x="574" y="116">48 kHz</text>
          <circle className="welcome-timeline-port welcome-timeline-port-in" cx="540" cy="106" r="4" />
        </g>

        <path className="explainer-native-chain-cable" d="M188 106 C 214 106, 242 106, 266 106" />
        <path className="explainer-native-chain-cable" d="M408 106 C 458 106, 500 106, 540 106" />
        <path className="explainer-native-direct" d="M188 140 C 300 232, 450 232, 540 140" pathLength={1} />
        <path className="explainer-native-flow" d="M188 140 C 300 232, 450 232, 540 140" pathLength={1} />

        <rect className="explainer-native-badge" x="340" y="142" width="128" height="22" rx="11" />
        <text className="explainer-native-badge-text" x="404" y="157" textAnchor="middle">Bit-perfect path</text>
        <rect className="explainer-native-toggle" x="256" y="146" width="66" height="18" rx="9" />
        <circle className="explainer-native-toggle-dot" cx="312" cy="155" r="6" />
        <text className="explainer-native-toggle-text" x="270" y="159">Native</text>

        <text className="explainer-mixer-caption" x="360" y="252" textAnchor="middle">Source and device formats match</text>
      </svg>
    </div>
  );
}

const VISUALS: Record<ExplainerId, ComponentType> = {
  mixer: MixerExplainer,
  sidechain: SidechainExplainer,
  vst3: Vst3Explainer,
  native: NativeExplainer,
};

export default function ExplainerDialog({ id, onClose }: { id: ExplainerId; onClose: () => void }) {
  const { t } = useI18n();
  const copy = COPY[id];
  const Visual = VISUALS[id];
  return (
    <Modal title={t(copy.title)} onClose={onClose} className="wide-modal explainer-modal">
      <p className="explainer-lead">{t(copy.lead)}</p>
      <Visual />
      <div className="modal-actions">
        <button className="primary-button" onClick={onClose}>{t('Got it')}</button>
      </div>
    </Modal>
  );
}
