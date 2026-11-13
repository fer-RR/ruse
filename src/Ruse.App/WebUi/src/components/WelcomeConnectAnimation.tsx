import { useId } from 'react';
import { useI18n } from '../i18n';

export default function WelcomeConnectAnimation({ className = '' }: { className?: string }) {
  const { t } = useI18n();
  const uid = useId().replace(/[^a-zA-Z0-9_-]/g, '');
  const sourceGradient = `welcome-source-${uid}`;
  const targetGradient = `welcome-target-${uid}`;
  const cableGradient = `welcome-cable-${uid}`;
  const glowGradient = `welcome-glow-${uid}`;
  const dots = `welcome-dots-${uid}`;
  const cable = 'M408 152 C 430 146, 470 158, 492 152';

  return (
    <div className={`welcome-drag ${className}`.trim()}>
      <svg
        className="welcome-drag-canvas"
        viewBox="0 0 720 300"
        role="img"
        aria-label={t('Drag from the OUT dot on a node to the IN dot on another node. The cable carries the sound.')}
      >
        <defs>
          <linearGradient id={sourceGradient} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#f0dc5a" />
            <stop offset="1" stopColor="#d69e2e" />
          </linearGradient>
          <linearGradient id={targetGradient} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#43e39a" />
            <stop offset="1" stopColor="#149a5d" />
          </linearGradient>
          <linearGradient id={cableGradient} x1="408" y1="152" x2="492" y2="152" gradientUnits="userSpaceOnUse">
            <stop offset="0" stopColor="#f0dc5a" />
            <stop offset="0.55" stopColor="#a3e635" />
            <stop offset="1" stopColor="#43e39a" />
          </linearGradient>
          <radialGradient id={glowGradient}>
            <stop offset="0" stopColor="#43e39a" stopOpacity="0.42" />
            <stop offset="1" stopColor="#43e39a" stopOpacity="0" />
          </radialGradient>
          <pattern id={dots} width="18" height="18" patternUnits="userSpaceOnUse">
            <circle className="welcome-drag-grid-dot" cx="1" cy="1" r="1" />
          </pattern>
        </defs>

        <rect className="welcome-drag-stage" x="0.5" y="0.5" width="719" height="299" rx="16" />

        <rect className="welcome-drag-panel" x="24" y="24" width="184" height="252" rx="14" />
        <text className="welcome-drag-label" x="42" y="49">{t('Library').toUpperCase()}</text>

        <g className="welcome-drag-library-source">
          <rect className="welcome-drag-card" x="40" y="66" width="152" height="58" rx="10" />
          <circle className="welcome-drag-bubble" cx="62" cy="95" r="14" />
          <g className="welcome-drag-glyph" transform="translate(53 86) scale(0.75)">
            <rect x="9" y="2" width="6" height="13" rx="3" />
            <path d="M5 10v2a7 7 0 0 0 14 0v-2" />
            <path d="M12 19v3" />
          </g>
          <rect className="welcome-drag-bar" x="84" y="82" width="88" height="6" rx="3" />
          <rect className="welcome-drag-bar welcome-drag-bar-dim" x="84" y="96" width="58" height="5" rx="2.5" />
        </g>

        <g className="welcome-drag-library-effect">
          <rect className="welcome-drag-card" x="40" y="134" width="152" height="58" rx="10" />
          <circle className="welcome-drag-bubble welcome-drag-bubble-violet" cx="62" cy="163" r="14" />
          <path className="welcome-drag-glyph welcome-drag-wave" d="M54 163c3-7 6 7 9 0s6-7 7 0" />
          <rect className="welcome-drag-bar" x="84" y="150" width="76" height="6" rx="3" />
          <rect className="welcome-drag-bar welcome-drag-bar-dim" x="84" y="164" width="48" height="5" rx="2.5" />
        </g>

        <g className="welcome-drag-library-output">
          <rect className="welcome-drag-card" x="40" y="202" width="152" height="58" rx="10" />
          <circle className="welcome-drag-bubble welcome-drag-bubble-amber" cx="62" cy="231" r="14" />
          <g className="welcome-drag-glyph" transform="translate(54 222) scale(0.75)">
            <path d="M11 5 6 9H3v6h3l5 4V5Z" />
            <path d="M15.5 8.5a5 5 0 0 1 0 7" />
            <path d="M18.5 5.5a9 9 0 0 1 0 13" />
          </g>
          <rect className="welcome-drag-bar" x="84" y="218" width="82" height="6" rx="3" />
          <rect className="welcome-drag-bar welcome-drag-bar-dim" x="84" y="232" width="52" height="5" rx="2.5" />
        </g>

        <rect className="welcome-drag-panel" x="224" y="24" width="472" height="252" rx="14" />
        <text className="welcome-drag-label" x="242" y="49">{t('Canvas').toUpperCase()}</text>
        <rect x="238" y="56" width="444" height="204" rx="10" fill={`url(#${dots})`} />

        <circle className="welcome-connect-halo" cx="562" cy="152" r="88" fill={`url(#${glowGradient})`} />

        <g className="welcome-connect-node">
          <rect x="268" y="116" width="140" height="72" rx="12" fill={`url(#${sourceGradient})`} stroke="rgba(255, 255, 255, 0.35)" />
          <circle cx="296" cy="152" r="14" fill="rgba(255, 255, 255, 0.22)" />
          <g className="welcome-connect-glyph welcome-mic-glyph" transform="translate(287 143) scale(0.75)">
            <rect x="9" y="2" width="6" height="13" rx="3" />
            <path d="M5 10v2a7 7 0 0 0 14 0v-2" />
            <path d="M12 19v3" />
          </g>
          <rect className="welcome-connect-bar welcome-mic-bar" x="318" y="139" width="72" height="6" rx="3" />
          <rect className="welcome-connect-bar welcome-connect-bar-dim welcome-mic-bar" x="318" y="151" width="48" height="5" rx="2.5" />
          <circle className="welcome-connect-port welcome-connect-port-out" cx="408" cy="152" r="6" />
        </g>

        <g className="welcome-connect-node welcome-connect-target">
          <rect x="492" y="116" width="140" height="72" rx="12" fill={`url(#${targetGradient})`} stroke="rgba(255, 255, 255, 0.35)" />
          <circle cx="520" cy="152" r="14" fill="rgba(255, 255, 255, 0.22)" />
          <g transform="translate(511 143) scale(0.75)">
            <g className="welcome-connect-speaker-icon">
              <path className="welcome-connect-glyph welcome-connect-speaker-fill" d="M11 5 6 9H3v6h3l5 4V5Z" />
              <path className="welcome-connect-glyph welcome-connect-speaker-wave" d="M15.5 8.5a5 5 0 0 1 0 7" />
              <path className="welcome-connect-glyph welcome-connect-speaker-wave" d="M18.5 5.5a9 9 0 0 1 0 13" />
            </g>
          </g>
          <rect className="welcome-connect-bar" x="542" y="139" width="72" height="6" rx="3" />
          <rect className="welcome-connect-bar welcome-connect-bar-dim" x="542" y="151" width="48" height="5" rx="2.5" />
          <circle className="welcome-connect-port welcome-connect-port-in" cx="492" cy="152" r="6" />
        </g>

        <path className="welcome-connect-cable" d={cable} pathLength={1} stroke={`url(#${cableGradient})`} />
        <path className="welcome-connect-flow" d={cable} pathLength={1} />

        <circle className="welcome-connect-port-ripple" cx="408" cy="152" r="13" />
        <circle className="welcome-connect-ring" cx="492" cy="152" r="14" />
        <circle className="welcome-connect-spark" cx="478" cy="134" r="2.5" />
        <circle className="welcome-connect-spark" cx="508" cy="134" r="2" />
        <circle className="welcome-connect-spark" cx="500" cy="172" r="2.2" />

        <g className="welcome-connect-waves">
          <path className="welcome-connect-wave welcome-connect-wave-a" d="M638 132 C 648 142, 648 162, 638 172" />
          <path className="welcome-connect-wave welcome-connect-wave-b" d="M648 124 C 662 138, 662 166, 648 180" />
          <path className="welcome-connect-wave welcome-connect-wave-c" d="M658 116 C 676 134, 676 170, 658 188" />
        </g>

        <g className="welcome-connect-motion">
          <g className="welcome-connect-path">
            <g className="welcome-connect-arrow">
              <g className="welcome-connect-tilt">
                <path d="M0 0 0 20 5 15 8 22 12 20 9 13 16 13Z" />
              </g>
            </g>
            <g transform="translate(-12 -14)">
              <g className="welcome-connect-grab">
                <path d="M6.5 12.5 4.9 10.7a1.5 1.5 0 0 1 2.3-1.9l2.1 2.5V6.5a1.5 1.5 0 0 1 3 0V11V5.5a1.5 1.5 0 0 1 3 0V11V7a1.5 1.5 0 0 1 3 0v4.5V9a1.5 1.5 0 0 1 3 0v5.3A7 7 0 0 1 15.8 22h-1.6a6.5 6.5 0 0 1-5.2-2.6l-2.5-3.3z" />
              </g>
            </g>
          </g>
        </g>
      </svg>
    </div>
  );
}
