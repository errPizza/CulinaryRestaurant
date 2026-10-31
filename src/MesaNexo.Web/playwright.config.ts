import { defineConfig } from '@playwright/test';
// Never reuses a server: another worktree's preview on this port would be tested instead of this code.
const port = process.env.WEB_PORT || '1420';
export default defineConfig({ testDir: './tests', workers: 1, timeout: 60000, expect: { timeout: 12000 }, reporter: [['list'], ['json', { outputFile: '../../evidence/hybrid-web-tests.json' }]], use: { baseURL: 'http://127.0.0.1:' + port, viewport: { width: 1360, height: 900 }, trace: 'retain-on-failure' }, webServer: { command: 'npx vite preview --host 127.0.0.1 --strictPort --port ' + port, url: 'http://127.0.0.1:' + port, reuseExistingServer: false, timeout: 30000 } });
