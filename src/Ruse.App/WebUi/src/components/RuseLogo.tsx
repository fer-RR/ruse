export default function RuseLogo({ size = 30 }: { size?: number }) {
  return (
    <svg
      className="brand-logo"
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 256 256"
      width={size}
      height={size}
      role="img"
      aria-label="Ruse"
    >
      <defs>
        <filter id="ruse-logo-glow" x="-40%" y="-40%" width="180%" height="180%">
          <feGaussianBlur stdDeviation="5" />
        </filter>
        <g
          id="ruse-logo-mark"
          fill="none"
          stroke="currentColor"
          strokeLinecap="round"
          strokeLinejoin="round"
        >
          <circle cx="128" cy="128" r="92" strokeWidth="17" />
          <path d="M128 128 L80 100" strokeWidth="27" />
        </g>
      </defs>
      <use href="#ruse-logo-mark" filter="url(#ruse-logo-glow)" opacity="0.75" />
      <use href="#ruse-logo-mark" />
    </svg>
  );
}
