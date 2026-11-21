import React from 'react';
import ReactDOM from 'react-dom/client';
import { ReactFlowProvider } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import App from './App';
import './themes/dark.css';
import './styles.css';
import { ensureThemeStyles } from './themeStyles';
import type { ThemeId } from './themes';
import { THEME_IDS } from './themes';

function storedTheme(): ThemeId {
  try {
    const value = localStorage.getItem('ruse.theme');
    return THEME_IDS.includes(value as ThemeId) ? value as ThemeId : 'dark';
  } catch {
    return 'dark';
  }
}

const root = ReactDOM.createRoot(document.getElementById('root')!);
const initialTheme = storedTheme();

function render(theme: ThemeId) {
  document.documentElement.dataset.theme = theme;
  root.render(
    <React.StrictMode>
      <ReactFlowProvider>
        <App />
      </ReactFlowProvider>
    </React.StrictMode>,
  );
}

if (initialTheme === 'dark') {
  render('dark');
} else {
  void ensureThemeStyles(initialTheme)
    .then(() => render(initialTheme))
    .catch(() => render('dark'));
}
