import { useEffect, useRef, useState } from 'react';
import { Check, Copy, Loader2, MessageSquare, Send } from 'lucide-react';
import { send, subscribe } from '../bridge';
import { useI18n } from '../i18n';
import type { ChatStateModel } from '../types';
import { nextTabId } from '../tabs';

const CHAT_TAB_IDS = ['host', 'join'] as const;

const emptyChat: ChatStateModel = {
  active: false, hosting: false, connected: false, mode: '', ownerNodeId: '',
  localName: '', peerName: '', pairCode: '', hostAddress: '', port: 7895,
  error: null, messages: [],
};

function formatTime(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? ''
    : date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export default function NetworkChat({ nodeId, port, peerHost, onPortChange }: {
  nodeId: string;
  port: number;
  peerHost: string;
  onPortChange: (port: number) => void;
}) {
  const { t } = useI18n();
  const [chat, setChat] = useState<ChatStateModel>(emptyChat);
  const [setupOpen, setSetupOpen] = useState(false);
  const [mode, setMode] = useState<'host' | 'join'>('host');
  const [name, setName] = useState(() => {
    try {
      return localStorage.getItem('ruse.chatName') ?? '';
    } catch {
      return '';
    }
  });
  const [host, setHost] = useState(peerHost || '127.0.0.1');
  const [code, setCode] = useState('');
  const [portDraft, setPortDraft] = useState(String(port));
  const [draft, setDraft] = useState('');
  const [copied, setCopied] = useState('');
  const [localError, setLocalError] = useState('');
  const messagesRef = useRef<HTMLDivElement>(null);
  const composerRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => subscribe((type, payload) => {
    if (type === 'chat-state') {
      setChat(payload as ChatStateModel);
    }
  }), []);

  useEffect(() => {
    send('chat-status');
  }, []);

  useEffect(() => {
    try {
      localStorage.setItem('ruse.chatName', name);
    } catch {
      // Name persistence is optional.
    }
  }, [name]);

  useEffect(() => {
    setPortDraft(String(port));
  }, [port]);

  useEffect(() => {
    if (peerHost) {
      setHost(current => !current || current === '127.0.0.1' ? peerHost : current);
    }
  }, [peerHost]);

  useEffect(() => {
    if (chat.connected) {
      setSetupOpen(false);
      setLocalError('');
      composerRef.current?.focus();
    }
  }, [chat.connected]);

  useEffect(() => {
    const list = messagesRef.current;
    if (list) list.scrollTop = list.scrollHeight;
  }, [chat.messages.length, chat.connected]);

  const mine = chat.ownerNodeId === nodeId;
  const active = mine && chat.active;
  const connected = mine && chat.connected;
  const busy = chat.active && !mine;
  const error = localError || (mine ? chat.error : '');

  const commitPort = () => {
    const value = Number(portDraft);
    if (!Number.isInteger(value) || value < 1 || value > 65535) {
      setPortDraft(String(port));
      setLocalError(t('Choose a port between 1 and 65535.'));
      return;
    }

    setLocalError('');
    if (value !== port) onPortChange(value);
  };

  const startHost = () => {
    const value = Number(portDraft);
    if (!Number.isInteger(value) || value < 1 || value > 65535) {
      setLocalError(t('Choose a port between 1 and 65535.'));
      return;
    }

    setLocalError('');
    if (value !== port) onPortChange(value);
    send('chat-host', { nodeId, name: name.trim(), port: value, loopbackOnly: false });
  };

  const startJoin = () => {
    const value = Number(portDraft);
    if (!host.trim()) {
      setLocalError(t('Enter the host address.'));
      return;
    }

    if (!Number.isInteger(value) || value < 1 || value > 65535) {
      setLocalError(t('Choose a port between 1 and 65535.'));
      return;
    }

    if (!code.trim()) {
      setLocalError(t('Enter the pairing code.'));
      return;
    }

    setLocalError('');
    if (value !== port) onPortChange(value);
    send('chat-join', {
      nodeId,
      name: name.trim(),
      host: host.trim(),
      port: value,
      code: code.trim(),
    });
  };

  const copy = async (text: string, key: string) => {
    try {
      await navigator.clipboard?.writeText(text);
      setCopied(key);
      window.setTimeout(() => setCopied(current => current === key ? '' : current), 1500);
    } catch {
      setCopied('');
    }
  };

  const sendMessage = () => {
    const text = draft.trim();
    if (!text || !connected) return;
    send('chat-send', { text });
    setDraft('');
  };

  return (
    <section className={`network-card network-chat ${connected ? 'network-chat-connected' : ''}`}>
      <header className="network-card-head">
        <span className="network-card-icon"><MessageSquare size={13} /></span>
        <strong>{t('Text')}</strong>
        <span className={`network-pill ${connected ? 'online' : active ? 'waiting' : ''}`}>
          <i />
          {connected ? chat.peerName : active ? (chat.hosting ? t('Hosting') : t('Connecting...')) : busy ? t('Text busy') : t('Disconnected')}
        </span>
      </header>

      <label className="network-field network-chat-port-field">
        <span>{t('Text port')}</span>
        <input
          type="number"
          min={1}
          max={65535}
          value={portDraft}
          disabled={active}
          onChange={event => setPortDraft(event.target.value)}
          onBlur={commitPort}
          onKeyDown={event => {
            if (event.key === 'Enter') {
              event.currentTarget.blur();
            }
          }}
        />
      </label>

      {error && <div className="network-chat-error" role="alert">{error}</div>}

      {!active && !setupOpen && (
        <button type="button" className="network-connect off" onClick={() => setSetupOpen(true)}>
          <MessageSquare size={13} /> {t('Connect text')}
        </button>
      )}

      {!active && setupOpen && (
        <div
          className="network-chat-setup"
          id="network-chat-setup"
          role="tabpanel"
          aria-labelledby={`network-chat-tab-${mode}`}
        >
          <div
            className="network-chat-tabs"
            role="tablist"
            aria-label={t('Chat mode')}
            onKeyDown={event => {
              const next = nextTabId(CHAT_TAB_IDS, mode, event.key);
              if (!next) return;
              event.preventDefault();
              setMode(next);
              setLocalError('');
              document.getElementById(`network-chat-tab-${next}`)?.focus();
            }}
          >
            <button id="network-chat-tab-host" type="button" role="tab" aria-selected={mode === 'host'} aria-controls="network-chat-setup" tabIndex={mode === 'host' ? 0 : -1} className={mode === 'host' ? 'active' : ''} onClick={() => { setMode('host'); setLocalError(''); }}>{t('Host')}</button>
            <button id="network-chat-tab-join" type="button" role="tab" aria-selected={mode === 'join'} aria-controls="network-chat-setup" tabIndex={mode === 'join' ? 0 : -1} className={mode === 'join' ? 'active' : ''} onClick={() => { setMode('join'); setLocalError(''); }}>{t('Join')}</button>
          </div>

          <label className="network-row network-chat-field">
            <span>{t('Your name')}</span>
            <input className="nowheel" value={name} maxLength={32} placeholder={t('Ruse')} onChange={event => setName(event.target.value)} />
          </label>

          {mode === 'join' && (
            <>
              <label className="network-row network-chat-field">
                <span>{t('Host address')}</span>
                <input className="nowheel" value={host} maxLength={255} autoComplete="off" spellCheck={false} onChange={event => setHost(event.target.value)} />
              </label>
              <label className="network-row network-chat-field">
                <span>{t('Pairing code')}</span>
                <input className="nowheel" value={code} maxLength={6} inputMode="numeric" autoComplete="off" placeholder="000000" onChange={event => setCode(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') startJoin(); }} />
              </label>
            </>
          )}

          <div className="network-actions">
            <button type="button" className="small-button" onClick={() => { setSetupOpen(false); setLocalError(''); }}>{t('Cancel')}</button>
            <button type="button" className="primary-button compact-button" onClick={mode === 'host' ? startHost : startJoin}>
              {mode === 'host' ? t('Host text') : t('Join text')}
            </button>
          </div>
          <p className="network-chat-note">{t('LAN only. Messages are not encrypted.')}</p>
        </div>
      )}

      {active && !connected && (
        <div className="network-chat-wait">
          {chat.hosting ? (
            <>
              <span>{t('Share this code with the other Ruse.')}</span>
              <button type="button" className="network-chat-code" title={t('Copy code')} aria-label={t('Copy code')} onClick={() => copy(chat.pairCode, 'code')}>
                <span>{chat.pairCode}</span>
                {copied === 'code' ? <Check size={13} /> : <Copy size={13} />}
              </button>
              <button type="button" className="network-chat-address" title={t('Copy address')} aria-label={t('Copy address')} onClick={() => copy(`${chat.hostAddress}:${chat.port}`, 'address')}>
                <span>{chat.hostAddress}:{chat.port}</span>
                {copied === 'address' ? <Check size={11} /> : <Copy size={11} />}
              </button>
            </>
          ) : (
            <>
              <Loader2 size={16} className="network-spin" />
              <span>{t('Connecting...')}</span>
            </>
          )}
        </div>
      )}

      {connected && (
        <>
          <div className="network-chat-messages" ref={messagesRef} role="log" aria-live="polite" aria-relevant="additions">
            {chat.messages.length === 0 && <div className="network-chat-empty">{t('No messages yet.')}</div>}
            {chat.messages.map(message => (
              <div className={`network-chat-message ${message.mine ? 'mine' : ''}`} key={message.id}>
                {!message.mine && <span>{message.sender}</span>}
                <p>{message.text}</p>
                <time dateTime={message.sentAt}>{formatTime(message.sentAt)}</time>
              </div>
            ))}
          </div>
          <form className="network-chat-compose" onSubmit={event => { event.preventDefault(); sendMessage(); }}>
            <textarea
              ref={composerRef}
              rows={2}
              maxLength={4000}
              value={draft}
              placeholder={t('Message')}
              aria-label={t('Message')}
              onChange={event => setDraft(event.target.value)}
              onKeyDown={event => {
                if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
                  event.preventDefault();
                  sendMessage();
                }
              }}
            />
            <button type="submit" className="primary-button compact-button network-chat-send" disabled={!draft.trim()} aria-label={t('Send')} title={t('Send')}>
              <Send size={13} />
            </button>
          </form>
        </>
      )}

      {active && (
        <button type="button" className="network-connect on" onClick={() => send('chat-stop')}>
          <MessageSquare size={13} /> {t('Disconnect text')}
        </button>
      )}
    </section>
  );
}
