import { useId } from 'react';
import { useI18n } from '../i18n';

export default function WelcomeTimelineAnimation({ className = '' }: { className?: string }) {
  const { t } = useI18n();
  const uid = useId().replace(/[^a-zA-Z0-9_-]/g, '');
  const glow = `timeline-glow-${uid}`;
  const clip = `timeline-clip-${uid}`;
  const micGradient = `timeline-mic-${uid}`;
  const effectGradient = `timeline-effect-${uid}`;
  const speakerGradient = `timeline-speaker-${uid}`;
  const dots = `timeline-dots-${uid}`;
  const lead = t('Open the Timeline, add a track, drop samples or recordings, then press Space to play. Drag an effect onto a cable to build an effect chain.');

  return (
    <div className={`welcome-drag ${className}`.trim()}>
      <svg
        className="welcome-drag-canvas"
        viewBox="0 0 720 320"
        role="img"
        aria-label={lead}
      >
        <defs>
          <radialGradient id={glow}>
            <stop offset="0" stopColor="#43e39a" stopOpacity="0.4" />
            <stop offset="1" stopColor="#43e39a" stopOpacity="0" />
          </radialGradient>
          <linearGradient id={clip} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#bfe8ff" />
            <stop offset="1" stopColor="#64b5d8" />
          </linearGradient>
          <linearGradient id={micGradient} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#f0dc5a" />
            <stop offset="1" stopColor="#d69e2e" />
          </linearGradient>
          <linearGradient id={effectGradient} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#e5ad51" />
            <stop offset="1" stopColor="#c05621" />
          </linearGradient>
          <linearGradient id={speakerGradient} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#43e39a" />
            <stop offset="1" stopColor="#149a5d" />
          </linearGradient>
          <pattern id={dots} width="18" height="18" patternUnits="userSpaceOnUse">
            <circle className="welcome-drag-grid-dot" cx="1" cy="1" r="1" />
          </pattern>
        </defs>

        <rect className="welcome-drag-stage" x="0.5" y="0.5" width="719" height="319" rx="16" />

        {/* Effect chain */}
        <rect className="welcome-drag-panel" x="24" y="20" width="672" height="128" rx="14" />
        <text className="welcome-drag-label" x="42" y="40">{t('Effect chain').toUpperCase()}</text>
        <rect x="238" y="48" width="440" height="88" rx="10" fill={`url(#${dots})`} />

        <rect className="welcome-drag-panel" x="36" y="50" width="112" height="86" rx="10" />
        <text className="welcome-drag-label" x="48" y="65">FX</text>
        <rect className="welcome-timeline-fx-source" x="46" y="72" width="92" height="22" rx="6" />
        <text className="welcome-timeline-fx-text" x="56" y="87">High-pass</text>
        <rect className="welcome-timeline-fx-source" x="46" y="102" width="92" height="22" rx="6" />
        <text className="welcome-timeline-fx-text" x="56" y="117">Compressor</text>

        <g className="welcome-timeline-node">
          <rect x="178" y="52" width="84" height="52" rx="10" style={{ fill: `url(#${micGradient})`, stroke: 'rgba(255,255,255,0.35)' }} />
          <circle className="welcome-timeline-glyph-bubble" cx="194" cy="78" r="8" />
          <path className="welcome-timeline-glyph" d="M191 76v4M197 76v4M189 77v2a5 5 0 0 0 10 0v-2" />
          <text className="welcome-timeline-node-title" x="208" y="82">Mic</text>
          <circle className="welcome-timeline-port" cx="262" cy="78" r="4" />
        </g>

        <g className="welcome-timeline-node welcome-timeline-node-hp">
          <rect x="290" y="52" width="96" height="52" rx="10" style={{ fill: `url(#${effectGradient})`, stroke: 'rgba(255,255,255,0.35)' }} />
          <text className="welcome-timeline-node-title" x="302" y="82">High-pass</text>
          <circle className="welcome-timeline-port welcome-timeline-port-in" cx="290" cy="78" r="4" />
          <circle className="welcome-timeline-port" cx="386" cy="78" r="4" />
        </g>

        <g className="welcome-timeline-node welcome-timeline-node-comp">
          <rect x="408" y="52" width="96" height="52" rx="10" style={{ fill: `url(#${effectGradient})`, stroke: 'rgba(255,255,255,0.35)' }} />
          <text className="welcome-timeline-node-title" x="420" y="82">Compressor</text>
          <circle className="welcome-timeline-port welcome-timeline-port-in" cx="408" cy="78" r="4" />
          <circle className="welcome-timeline-port" cx="504" cy="78" r="4" />
        </g>

        <g className="welcome-timeline-node">
          <rect x="536" y="52" width="104" height="52" rx="10" style={{ fill: `url(#${speakerGradient})`, stroke: 'rgba(255,255,255,0.35)' }} />
          <circle className="welcome-timeline-glyph-bubble" cx="552" cy="78" r="8" />
          <path className="welcome-timeline-glyph" d="M550 75l-4 3h-2v4h2l4 3v-10ZM557 76a5 5 0 0 1 0 8" />
          <text className="welcome-timeline-node-title" x="566" y="82">Speakers</text>
          <circle className="welcome-timeline-port welcome-timeline-port-in" cx="536" cy="78" r="4" />
        </g>

        <path className="welcome-timeline-cable welcome-timeline-cable-direct" d="M262 78 C 370 78, 430 78, 536 78" />
        <path className="welcome-timeline-cable welcome-timeline-cable-mic-hp" d="M262 78 C 272 78, 280 78, 290 78" />
        <path className="welcome-timeline-cable welcome-timeline-cable-hp-direct" d="M386 78 C 450 78, 490 78, 536 78" />
        <path className="welcome-timeline-cable welcome-timeline-cable-hp-comp" d="M386 78 C 394 78, 400 78, 408 78" />
        <path className="welcome-timeline-cable welcome-timeline-cable-comp-speakers" d="M504 78 C 514 78, 526 78, 536 78" />
        <path className="welcome-timeline-cable-flow welcome-timeline-flow-mic" d="M262 78 C 272 78, 280 78, 290 78" pathLength={1} />
        <path className="welcome-timeline-cable-flow welcome-timeline-flow-comp" d="M386 78 C 394 78, 400 78, 408 78" pathLength={1} />
        <path className="welcome-timeline-cable-flow welcome-timeline-flow-comp" d="M504 78 C 514 78, 526 78, 536 78" pathLength={1} />
        <circle className="welcome-timeline-port-glow" cx="386" cy="78" r="16" fill={`url(#${glow})`} />

        <g className="welcome-timeline-fx-ghost welcome-timeline-fx-ghost-hp">
          <rect x="-46" y="-11" width="92" height="22" rx="6" />
          <text x="0" y="4">High-pass</text>
        </g>
        <g className="welcome-timeline-fx-ghost welcome-timeline-fx-ghost-comp">
          <rect x="-48" y="-11" width="96" height="22" rx="6" />
          <text x="0" y="4">Compressor</text>
        </g>

        {/* Timeline */}
        <rect className="welcome-drag-panel" x="24" y="160" width="672" height="144" rx="14" />
        <text className="welcome-drag-label" x="42" y="181">{t('Timeline').toUpperCase()}</text>
        <rect className="welcome-timeline-key" x="590" y="166" width="88" height="20" rx="5" />
        <text className="welcome-timeline-key-text" x="634" y="180">SPACE</text>

        <g className="welcome-timeline-add">
          <rect x="42" y="190" width="92" height="18" rx="9" />
          <text x="88" y="203">+ Add track</text>
        </g>

        <text className="welcome-timeline-track-label" x="42" y="232">1</text>
        <text className="welcome-timeline-track-label" x="42" y="268">2</text>
        <rect className="welcome-timeline-lane" x="140" y="214" width="540" height="28" rx="6" />
        <rect className="welcome-timeline-lane" x="140" y="250" width="540" height="28" rx="6" />

        <g className="welcome-timeline-clip-ghost">
          <rect x="-90" y="-11" width="180" height="22" rx="5" />
        </g>

        <g className="welcome-timeline-clip">
          <rect x="180" y="214" width="180" height="24" rx="5" fill={`url(#${clip})`} />
          <path className="welcome-timeline-wave" d="M188 232c5-10 9 10 14 0s9 10 14 0 9 10 14 0 9 10 14 0 9 10 14 0 9 10 14 0 9 10 14 0 9 10 14 0 9 10 14 0 9 10 14 0 9 10 14 0 9 10 14 0" />
        </g>
        <g className="welcome-timeline-clip welcome-timeline-clip-existing">
          <rect x="330" y="250" width="150" height="24" rx="5" fill={`url(#${clip})`} />
          <path className="welcome-timeline-wave" d="M338 268c5-10 9 10 14 0s9 10 14 0 9 10 14 0 9 10 14 0 9 10 14 0 9 10 14 0 9 10 14 0 9 10 14 0 9 10 14 0 9 10 14 0 9 10 14 0" />
        </g>

        <g className="welcome-timeline-playhead">
          <line x1="150" y1="202" x2="150" y2="290" />
          <path d="M144 200l6-8 6 8z" />
        </g>
        <text className="welcome-timeline-hint" x="684" y="295">Drop clips · Space to play</text>
      </svg>
    </div>
  );
}
