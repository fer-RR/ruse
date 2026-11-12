import { useId } from 'react';
import { translate, useI18n } from '../i18n';
import type { Language } from '../i18n';

export default function WelcomeExamplesAnimation({ className = '' }: { className?: string }) {
  const { t, language } = useI18n();
  const uid = useId().replace(/[^a-zA-Z0-9_-]/g, '');
  const sourceGradient = `examples-source-${uid}`;
  const targetGradient = `examples-target-${uid}`;
  const effectGradient = `examples-effect-${uid}`;
  const effectGlow = `examples-effect-glow-${uid}`;
  const recorderGlow = `examples-recorder-glow-${uid}`;
  const networkGradient = `examples-network-${uid}`;
  const networkCableGradient = `examples-network-cable-${uid}`;
  const appGradient = `examples-app-${uid}`;
  const graphClip = `examples-graph-clip-${uid}`;
  const dots = `examples-dots-${uid}`;
  const targetLanguage: Language = language === 'en' ? 'es' : 'en';
  const transcript = t('Welcome to Ruse');
  const translation = translate(targetLanguage, 'Welcome to Ruse');

  return (
    <div className={`welcome-drag ${className}`.trim()}>
      <svg
        className="welcome-drag-canvas"
        viewBox="0 0 720 300"
        role="img"
        aria-label={t('Effects, transcription, translation, recording and network audio, all on the canvas.')}
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
          <linearGradient id={effectGradient} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#e5ad51" />
            <stop offset="1" stopColor="#c05621" />
          </linearGradient>
          <radialGradient id={effectGlow}>
            <stop offset="0" stopColor="#e5ad51" stopOpacity="0.5" />
            <stop offset="1" stopColor="#e5ad51" stopOpacity="0" />
          </radialGradient>
          <radialGradient id={recorderGlow}>
            <stop offset="0" stopColor="#ff8b72" stopOpacity="0.42" />
            <stop offset="1" stopColor="#ff8b72" stopOpacity="0" />
          </radialGradient>
          <linearGradient id={networkGradient} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#4fd1c5" />
            <stop offset="1" stopColor="#0e7490" />
          </linearGradient>
          <linearGradient id={networkCableGradient} x1="0" y1="0" x2="1" y2="0">
            <stop offset="0" stopColor="#4fd1c5" />
            <stop offset="1" stopColor="#43e39a" />
          </linearGradient>
          <linearGradient id={appGradient} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#5b8def" />
            <stop offset="1" stopColor="#3b6fd4" />
          </linearGradient>
          <clipPath id={graphClip}>
            <rect x="24" y="24" width="672" height="252" rx="14" />
          </clipPath>
          <pattern id={dots} width="18" height="18" patternUnits="userSpaceOnUse">
            <circle className="welcome-drag-grid-dot" cx="1" cy="1" r="1" />
          </pattern>
        </defs>

        <rect className="welcome-drag-stage" x="0.5" y="0.5" width="719" height="299" rx="16" />
        <rect className="welcome-drag-panel" x="24" y="24" width="672" height="252" rx="14" />
        <rect x="38" y="38" width="644" height="224" rx="10" fill={`url(#${dots})`} />

        <g className="welcome-ex-scene welcome-ex-a">
          <g className="welcome-ex-a-left">
            <rect x="150" y="112" width="136" height="76" rx="12" fill={`url(#${sourceGradient})`} stroke="rgba(255, 255, 255, 0.35)" />
            <circle cx="176" cy="150" r="14" fill="rgba(255, 255, 255, 0.22)" />
            <g className="welcome-connect-glyph welcome-mic-glyph" transform="translate(167 141) scale(0.75)">
              <rect x="9" y="2" width="6" height="13" rx="3" />
              <path d="M5 10v2a7 7 0 0 0 14 0v-2" />
              <path d="M12 19v3" />
            </g>
            <rect className="welcome-connect-bar welcome-mic-bar" x="198" y="137" width="70" height="6" rx="3" />
            <rect className="welcome-connect-bar welcome-connect-bar-dim welcome-mic-bar" x="198" y="151" width="46" height="5" rx="2.5" />
            <circle className="welcome-connect-port welcome-connect-port-out" cx="286" cy="150" r="6" />
          </g>

          <g className="welcome-ex-a-right">
            <rect x="434" y="112" width="136" height="76" rx="12" fill={`url(#${targetGradient})`} stroke="rgba(255, 255, 255, 0.35)" />
            <circle cx="460" cy="150" r="14" fill="rgba(255, 255, 255, 0.22)" />
            <g transform="translate(451 141) scale(0.75)">
              <path className="welcome-connect-glyph welcome-connect-speaker-fill" d="M11 5 6 9H3v6h3l5 4V5Z" />
              <path className="welcome-connect-glyph" d="M15.5 8.5a5 5 0 0 1 0 7" />
              <path className="welcome-connect-glyph" d="M18.5 5.5a9 9 0 0 1 0 13" />
            </g>
            <rect className="welcome-connect-bar" x="482" y="137" width="70" height="6" rx="3" />
            <rect className="welcome-connect-bar welcome-connect-bar-dim" x="482" y="151" width="46" height="5" rx="2.5" />
            <circle className="welcome-connect-port welcome-connect-port-in" cx="434" cy="150" r="6" />
          </g>

          <path className="welcome-ex-direct" d="M286 150 C 330 142, 390 158, 434 150" pathLength={1} />
          <circle className="welcome-ex-effect-halo" cx="360" cy="150" r="72" fill={`url(#${effectGlow})`} />
          <g className="welcome-ex-effect">
            <rect x="305" y="112" width="110" height="76" rx="12" fill={`url(#${effectGradient})`} stroke="rgba(255, 255, 255, 0.35)" />
            <rect className="welcome-ex-eq-bar welcome-ex-eq-1" x="338" y="156" width="8" height="18" rx="2" />
            <rect className="welcome-ex-eq-bar welcome-ex-eq-2" x="349" y="142" width="8" height="32" rx="2" />
            <rect className="welcome-ex-eq-bar welcome-ex-eq-3" x="360" y="150" width="8" height="24" rx="2" />
            <circle className="welcome-connect-port welcome-connect-port-out" cx="305" cy="150" r="6" />
            <circle className="welcome-connect-port welcome-connect-port-in" cx="415" cy="150" r="6" />
          </g>
          <circle className="welcome-ex-effect-pulse" cx="360" cy="150" r="46" />
          <path className="welcome-ex-chain welcome-ex-chain-a" d="M222 150 C 250 142, 280 158, 305 150" pathLength={1} />
          <path className="welcome-ex-chain welcome-ex-chain-b" d="M415 150 C 445 142, 475 158, 498 150" pathLength={1} />
          <path className="welcome-ex-flow" d="M222 150 C 250 142, 280 158, 305 150" pathLength={1} />
          <path className="welcome-ex-flow" d="M415 150 C 445 142, 475 158, 498 150" pathLength={1} />
        </g>

        <g className="welcome-ex-scene welcome-ex-b">
          <g className="welcome-ex-source">
            <rect x="120" y="112" width="136" height="76" rx="12" fill={`url(#${sourceGradient})`} stroke="rgba(255, 255, 255, 0.35)" />
            <circle cx="146" cy="150" r="14" fill="rgba(255, 255, 255, 0.22)" />
            <g className="welcome-connect-glyph welcome-mic-glyph" transform="translate(137 141) scale(0.75)">
              <rect x="9" y="2" width="6" height="13" rx="3" />
              <path d="M5 10v2a7 7 0 0 0 14 0v-2" />
              <path d="M12 19v3" />
            </g>
            <rect className="welcome-connect-bar welcome-mic-bar" x="168" y="137" width="70" height="6" rx="3" />
            <rect className="welcome-connect-bar welcome-connect-bar-dim welcome-mic-bar" x="168" y="151" width="46" height="5" rx="2.5" />
            <circle className="welcome-connect-port welcome-connect-port-out" cx="256" cy="150" r="6" />
          </g>
          <circle className="welcome-ex-listening" cx="146" cy="150" r="18" />
          <path className="welcome-ex-cable" d="M256 150 C 310 142, 340 158, 390 150" pathLength={1} />

          <g className="welcome-ex-transcriber">
            <rect className="welcome-ex-card welcome-ex-card-transcriber" x="390" y="100" width="220" height="100" rx="12" />
            <circle className="welcome-ex-badge" cx="408" cy="116" r="8" />
            <path className="welcome-ex-badge-glyph" d="M404 114h8M404 118h5" />
            <rect className="welcome-connect-bar" x="422" y="113" width="62" height="5" rx="2.5" />
            <circle className="welcome-ex-status" cx="590" cy="116" r="3" />
            <rect className="welcome-ex-field" x="404" y="134" width="192" height="44" rx="8" />
            <text className="welcome-ex-text welcome-ex-type-transcript" x="414" y="162">{transcript}</text>
            <circle className="welcome-connect-port welcome-connect-port-in" cx="390" cy="150" r="6" />
            <circle className="welcome-connect-port welcome-connect-port-out" cx="610" cy="150" r="6" />
          </g>

          <path className="welcome-ex-link" d="M350 150 C 362 146, 378 154, 390 150" pathLength={1} />

          <g className="welcome-ex-translator">
            <rect className="welcome-ex-card welcome-ex-card-translator" x="390" y="100" width="200" height="100" rx="12" />
            <circle className="welcome-ex-badge welcome-ex-badge-translator" cx="408" cy="116" r="8" />
            <g className="welcome-ex-globe">
              <circle cx="408" cy="116" r="5" />
              <path d="M403 116h10M408 111a5 5 0 0 1 0 10M408 111a5 5 0 0 0 0 10" />
            </g>
            <rect className="welcome-connect-bar" x="422" y="113" width="56" height="5" rx="2.5" />
            <rect className="welcome-ex-field" x="404" y="134" width="172" height="44" rx="8" />
            <text className="welcome-ex-text welcome-ex-type-translation" x="414" y="162">{translation}</text>
            <circle className="welcome-connect-port welcome-connect-port-in" cx="390" cy="150" r="6" />
          </g>
        </g>

        <g className="welcome-ex-scene welcome-ex-d">
          <g>
            <rect x="130" y="112" width="136" height="76" rx="12" fill={`url(#${sourceGradient})`} stroke="rgba(255, 255, 255, 0.35)" />
            <circle cx="156" cy="150" r="14" fill="rgba(255, 255, 255, 0.22)" />
            <g className="welcome-connect-glyph welcome-mic-glyph" transform="translate(147 141) scale(0.75)">
              <rect x="9" y="2" width="6" height="13" rx="3" />
              <path d="M5 10v2a7 7 0 0 0 14 0v-2" />
              <path d="M12 19v3" />
            </g>
            <rect className="welcome-connect-bar welcome-mic-bar" x="178" y="137" width="70" height="6" rx="3" />
            <rect className="welcome-connect-bar welcome-connect-bar-dim welcome-mic-bar" x="178" y="151" width="46" height="5" rx="2.5" />
            <circle className="welcome-connect-port welcome-connect-port-out" cx="266" cy="150" r="6" />
          </g>

          <path className="welcome-ex-d-cable" d="M266 150 C 320 142, 380 158, 430 150" pathLength={1} />
          <path className="welcome-ex-d-flow" d="M266 150 C 320 142, 380 158, 430 150" pathLength={1} />

          <circle className="welcome-ex-d-halo" cx="545" cy="150" r="95" fill={`url(#${recorderGlow})`} />
          <g className="welcome-ex-recorder">
            <rect className="welcome-ex-card welcome-ex-card-recorder" x="430" y="96" width="230" height="108" rx="12" />
            <circle cx="446" cy="112" r="3" fill="#ff8b72" />
            <path className="welcome-ex-doc" d="M452 112h18l8 8v26h-26z" />
            <path className="welcome-ex-doc-fold" d="M470 112v8h8" />
            <path className="welcome-ex-doc-line" d="M458 130h14M458 136h14M458 142h9" />
            <rect className="welcome-connect-bar" x="492" y="116" width="118" height="6" rx="3" />
            <rect className="welcome-connect-bar welcome-connect-bar-dim" x="492" y="129" width="82" height="5" rx="2.5" />
            <rect className="welcome-ex-progress-track" x="452" y="172" width="192" height="8" rx="4" />
            <rect className="welcome-ex-progress" x="452" y="172" width="192" height="8" rx="4" />
            <circle className="welcome-connect-port welcome-connect-port-in" cx="430" cy="150" r="6" />
            <g className="welcome-ex-check">
              <circle cx="478" cy="146" r="10" fill="#43e39a" />
              <path d="M473 146l3.5 4 6.5-7" />
            </g>
          </g>
          <circle className="welcome-ex-d-pulse" cx="545" cy="150" r="58" />

          <g className="welcome-ex-file-card">
            <rect x="-11" y="-15" width="22" height="30" rx="4" fill="#ffe4dc" stroke="#ff8b72" />
            <path className="welcome-ex-file-fold" d="M3 -15v7h7" />
            <path className="welcome-ex-file-line" d="M-5 1h10M-5 6h7" />
          </g>
        </g>

        <g className="welcome-ex-scene welcome-ex-n">
          <path
            className="welcome-ex-n-incoming"
            d="M0 152 C 90 152, 180 152, 270 152"
            pathLength={1}
          />

          <g className="welcome-ex-n-ip">
            <rect className="welcome-ex-n-card" x="270" y="104" width="180" height="96" rx="12" />
            <circle className="welcome-ex-n-badge" cx="288" cy="120" r="8" />
            <g className="welcome-ex-n-globe">
              <circle cx="288" cy="120" r="5" />
              <path d="M283 120h10M288 115a5 5 0 0 1 0 10M288 115a5 5 0 0 0 0 10" />
            </g>
            <text className="welcome-ex-n-lan" x="302" y="123">LAN</text>
            <circle className="welcome-ex-n-status" cx="432" cy="120" r="3" />
            <rect className="welcome-ex-field" x="284" y="136" width="152" height="44" rx="8" />
            <text className="welcome-ex-ip welcome-ex-type-ip" x="294" y="163">192.168.1.42</text>
            <circle className="welcome-connect-port welcome-ex-n-port-in" cx="270" cy="152" r="6" />
          </g>

          <circle className="welcome-ex-n-flash" cx="360" cy="152" r="60" />

          <g className="welcome-ex-n-pair">
            <g className="welcome-ex-n-left">
              <rect x="140" y="117" width="120" height="70" rx="12" fill={`url(#${networkGradient})`} stroke="rgba(255, 255, 255, 0.35)" />
              <circle cx="162" cy="152" r="12" fill="rgba(255, 255, 255, 0.22)" />
              <g className="welcome-connect-glyph" transform="translate(154 144) scale(0.65)">
                <rect x="3" y="4" width="18" height="12" rx="2" />
                <path d="M9 20h6M12 16v4" />
              </g>
              <rect className="welcome-connect-bar" x="180" y="142" width="58" height="5" rx="2.5" />
              <rect className="welcome-connect-bar welcome-connect-bar-dim" x="180" y="154" width="38" height="4.5" rx="2.25" />
              <circle className="welcome-ex-n-dot" cx="246" cy="132" r="2.5" />
              <circle className="welcome-connect-port welcome-ex-n-port-out" cx="260" cy="152" r="6" />
            </g>
            <g className="welcome-ex-n-right">
              <rect x="460" y="117" width="120" height="70" rx="12" fill={`url(#${networkGradient})`} stroke="rgba(255, 255, 255, 0.35)" />
              <circle cx="482" cy="152" r="12" fill="rgba(255, 255, 255, 0.22)" />
              <g className="welcome-connect-glyph" transform="translate(474 144) scale(0.65)">
                <rect x="3" y="4" width="18" height="12" rx="2" />
                <path d="M9 20h6M12 16v4" />
              </g>
              <rect className="welcome-connect-bar" x="500" y="142" width="58" height="5" rx="2.5" />
              <rect className="welcome-connect-bar welcome-connect-bar-dim" x="500" y="154" width="38" height="4.5" rx="2.25" />
              <circle className="welcome-ex-n-dot" cx="566" cy="132" r="2.5" />
              <circle className="welcome-connect-port welcome-ex-n-port-in" cx="460" cy="152" r="6" />
            </g>
            <path className="welcome-ex-n-link" d="M260 152 C 330 142, 390 162, 460 152" pathLength={1} stroke={`url(#${networkCableGradient})`} />
            <path className="welcome-ex-n-flow welcome-ex-n-flow-fwd" d="M260 152 C 330 142, 390 162, 460 152" pathLength={1} />
            <path className="welcome-ex-n-flow welcome-ex-n-flow-rev" d="M260 152 C 330 142, 390 162, 460 152" pathLength={1} />
            <rect className="welcome-ex-packet welcome-ex-packet-fwd1" x="-3" y="-4" width="6" height="8" rx="1.5" />
            <rect className="welcome-ex-packet welcome-ex-packet-fwd2" x="-3" y="-4" width="6" height="8" rx="1.5" />
            <rect className="welcome-ex-packet welcome-ex-packet-rev1" x="-3" y="-4" width="6" height="8" rx="1.5" />
            <rect className="welcome-ex-packet welcome-ex-packet-rev2" x="-3" y="-4" width="6" height="8" rx="1.5" />
          </g>
        </g>

        <g className="welcome-ex-g">
          <g clipPath={`url(#${graphClip})`}>
            <g className="welcome-ex-g-world">
              <path className="welcome-ex-g-link" d="M176 87 C 190 87, 200 87, 214 87" stroke="#f0dc5a" />
              <path className="welcome-ex-g-link" d="M320 87 C 400 80, 500 79, 568 79" stroke="#e5ad51" />
              <path className="welcome-ex-g-link" d="M176 87 C 190 130, 200 183, 214 183" stroke="#f0dc5a" />
              <path className="welcome-ex-g-link" d="M176 205 C 190 198, 200 183, 214 183" stroke="#5b8def" />
              <path className="welcome-ex-g-link" d="M366 183 C 375 183, 385 183, 394 183" stroke="#e5ad51" />
              <path className="welcome-ex-g-link" d="M546 183 C 555 183, 560 181, 568 181" stroke="#73c8e8" />
              <path className="welcome-ex-g-link" d="M546 183 C 556 150, 560 100, 568 79" stroke="#73c8e8" />
              <path className="welcome-ex-g-link" d="M176 205 C 300 240, 430 255, 544 250" stroke="#5b8def" />

              <path className="welcome-ex-g-flow" d="M176 87 C 190 87, 200 87, 214 87" />
              <path className="welcome-ex-g-flow" d="M320 87 C 400 80, 500 79, 568 79" />
              <path className="welcome-ex-g-flow" d="M176 87 C 190 130, 200 183, 214 183" />
              <path className="welcome-ex-g-flow" d="M176 205 C 190 198, 200 183, 214 183" />
              <path className="welcome-ex-g-flow" d="M366 183 C 375 183, 385 183, 394 183" />
              <path className="welcome-ex-g-flow" d="M546 183 C 555 183, 560 181, 568 181" />
              <path className="welcome-ex-g-flow" d="M546 183 C 556 150, 560 100, 568 79" />
              <path className="welcome-ex-g-flow" d="M176 205 C 300 240, 430 255, 544 250" />

              <g>
                <rect x="64" y="56" width="112" height="62" rx="10" fill={`url(#${sourceGradient})`} stroke="rgba(255, 255, 255, 0.35)" />
                <circle cx="88" cy="87" r="12" fill="rgba(255, 255, 255, 0.22)" />
                <g className="welcome-connect-glyph welcome-mic-glyph" transform="translate(80 79) scale(0.6)">
                  <rect x="9" y="2" width="6" height="13" rx="3" />
                  <path d="M5 10v2a7 7 0 0 0 14 0v-2" />
                  <path d="M12 19v3" />
                </g>
                <rect className="welcome-connect-bar welcome-mic-bar" x="106" y="78" width="58" height="5" rx="2.5" />
                <rect className="welcome-connect-bar welcome-connect-bar-dim welcome-mic-bar" x="106" y="90" width="38" height="4.5" rx="2.25" />
                <circle className="welcome-connect-port" cx="176" cy="87" r="5" fill="#f0dc5a" />
              </g>

              <g>
                <rect x="64" y="174" width="112" height="62" rx="10" fill={`url(#${appGradient})`} stroke="rgba(255, 255, 255, 0.35)" />
                <circle cx="88" cy="205" r="12" fill="rgba(255, 255, 255, 0.22)" />
                <g className="welcome-connect-glyph" transform="translate(80 197) scale(0.6)">
                  <rect x="3" y="4" width="18" height="12" rx="2" />
                  <path d="M9 20h6M12 16v4" />
                </g>
                <rect className="welcome-connect-bar" x="106" y="196" width="58" height="5" rx="2.5" />
                <rect className="welcome-connect-bar welcome-connect-bar-dim" x="106" y="208" width="38" height="4.5" rx="2.25" />
                <circle className="welcome-connect-port" cx="176" cy="205" r="5" fill="#5b8def" />
              </g>

              <g>
                <rect x="214" y="56" width="106" height="62" rx="10" fill={`url(#${effectGradient})`} stroke="rgba(255, 255, 255, 0.35)" />
                <rect className="welcome-ex-eq-bar" x="242" y="92" width="6" height="12" rx="1.5" />
                <rect className="welcome-ex-eq-bar" x="252" y="82" width="6" height="22" rx="1.5" />
                <rect className="welcome-ex-eq-bar" x="262" y="88" width="6" height="16" rx="1.5" />
                <circle className="welcome-connect-port" cx="214" cy="87" r="5" fill="#e5ad51" />
                <circle className="welcome-connect-port" cx="320" cy="87" r="5" fill="#e5ad51" />
              </g>

              <g>
                <rect className="welcome-ex-card welcome-ex-card-transcriber" x="214" y="150" width="152" height="66" rx="10" />
                <circle className="welcome-ex-badge" cx="230" cy="166" r="7" />
                <path className="welcome-ex-badge-glyph" d="M226 164h8M226 168h5" />
                <rect className="welcome-connect-bar" x="244" y="163" width="60" height="5" rx="2.5" />
                <rect className="welcome-ex-field" x="226" y="178" width="128" height="26" rx="6" />
                <rect className="welcome-connect-bar" x="232" y="184" width="88" height="4" rx="2" />
                <rect className="welcome-connect-bar welcome-connect-bar-dim" x="232" y="192" width="56" height="4" rx="2" />
                <circle className="welcome-connect-port" cx="214" cy="183" r="5" fill="#e5ad51" />
                <circle className="welcome-connect-port" cx="366" cy="183" r="5" fill="#e5ad51" />
              </g>

              <g>
                <rect className="welcome-ex-card welcome-ex-card-translator" x="394" y="150" width="152" height="66" rx="10" />
                <circle className="welcome-ex-badge welcome-ex-badge-translator" cx="410" cy="166" r="7" />
                <g className="welcome-ex-globe">
                  <circle cx="410" cy="166" r="4.5" />
                  <path d="M405.5 166h9M410 161.5a4.5 4.5 0 0 1 0 9M410 161.5a4.5 4.5 0 0 0 0 9" />
                </g>
                <rect className="welcome-connect-bar" x="424" y="163" width="56" height="5" rx="2.5" />
                <rect className="welcome-ex-field" x="406" y="178" width="128" height="26" rx="6" />
                <rect className="welcome-connect-bar" x="412" y="184" width="88" height="4" rx="2" />
                <rect className="welcome-connect-bar welcome-connect-bar-dim" x="412" y="192" width="56" height="4" rx="2" />
                <circle className="welcome-connect-port" cx="394" cy="183" r="5" fill="#73c8e8" />
                <circle className="welcome-connect-port" cx="546" cy="183" r="5" fill="#73c8e8" />
              </g>

              <g>
                <rect x="568" y="48" width="112" height="62" rx="10" fill={`url(#${targetGradient})`} stroke="rgba(255, 255, 255, 0.35)" />
                <circle cx="592" cy="79" r="12" fill="rgba(255, 255, 255, 0.22)" />
                <g transform="translate(584 71) scale(0.6)">
                  <path className="welcome-connect-glyph welcome-connect-speaker-fill" d="M11 5 6 9H3v6h3l5 4V5Z" />
                  <path className="welcome-connect-glyph" d="M15.5 8.5a5 5 0 0 1 0 7" />
                  <path className="welcome-connect-glyph" d="M18.5 5.5a9 9 0 0 1 0 13" />
                </g>
                <rect className="welcome-connect-bar" x="610" y="70" width="58" height="5" rx="2.5" />
                <rect className="welcome-connect-bar welcome-connect-bar-dim" x="610" y="82" width="38" height="4.5" rx="2.25" />
                <circle className="welcome-connect-port" cx="568" cy="79" r="5" fill="#43e39a" />
              </g>

              <g>
                <rect x="568" y="150" width="112" height="62" rx="10" fill={`url(#${targetGradient})`} stroke="rgba(255, 255, 255, 0.35)" />
                <circle cx="592" cy="181" r="12" fill="rgba(255, 255, 255, 0.22)" />
                <g transform="translate(584 173) scale(0.6)">
                  <path className="welcome-connect-glyph welcome-connect-speaker-fill" d="M11 5 6 9H3v6h3l5 4V5Z" />
                  <path className="welcome-connect-glyph" d="M15.5 8.5a5 5 0 0 1 0 7" />
                  <path className="welcome-connect-glyph" d="M18.5 5.5a9 9 0 0 1 0 13" />
                </g>
                <rect className="welcome-connect-bar" x="610" y="172" width="58" height="5" rx="2.5" />
                <rect className="welcome-connect-bar welcome-connect-bar-dim" x="610" y="184" width="38" height="4.5" rx="2.25" />
                <circle className="welcome-connect-port" cx="568" cy="181" r="5" fill="#43e39a" />
              </g>

              <g>
                <rect className="welcome-ex-card welcome-ex-card-recorder" x="544" y="228" width="136" height="44" rx="10" />
                <path className="welcome-ex-doc" d="M556 236h10l5 5v17h-15z" />
                <path className="welcome-ex-doc-fold" d="M566 236v5h5" />
                <path className="welcome-ex-doc-line" d="M560 248h8M560 252h8" />
                <rect className="welcome-connect-bar" x="574" y="238" width="60" height="5" rx="2.5" />
                <rect className="welcome-connect-bar welcome-connect-bar-dim" x="574" y="248" width="42" height="4" rx="2" />
                <rect x="574" y="258" width="52" height="4" rx="2" fill="#ff8b72" />
                <circle className="welcome-connect-port" cx="544" cy="250" r="5" fill="#ff8b72" />
              </g>

              <rect className="welcome-ex-g-packet welcome-ex-g-packet-a" x="-3" y="-4" width="6" height="8" rx="1.5" />
              <rect className="welcome-ex-g-packet welcome-ex-g-packet-b" x="-3" y="-4" width="6" height="8" rx="1.5" />
              <rect className="welcome-ex-g-packet welcome-ex-g-packet-c" x="-3" y="-4" width="6" height="8" rx="1.5" />
              <rect className="welcome-ex-g-packet welcome-ex-g-packet-d" x="-3" y="-4" width="6" height="8" rx="1.5" />
            </g>
          </g>
        </g>
      </svg>
    </div>
  );
}
