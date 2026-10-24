import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';
// Two pages: the operation (index.html) and the customer-facing display opened on a second monitor (cliente.html).
export default defineConfig({ plugins: [react()], base: './', server: { host: '127.0.0.1', port: 1420, strictPort: true, watch: { ignored: ['**/src-tauri/**'] } }, clearScreen: false, build: { rollupOptions: { input: { main: resolve(__dirname, 'index.html'), cliente: resolve(__dirname, 'cliente.html') } } } });
