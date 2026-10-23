import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

function git(...args: string[]): string | null {
  try {
    return execFileSync('git', args, {
      cwd: fileURLToPath(new URL('.', import.meta.url)),
      encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore']
    }).trim();
  } catch {
    return null;
  }
}

export default defineConfig(({ command }) => ({
  plugins: [vue()],
  // Embed the identity in the artifact so a browser reload never changes its build date.
  define: {
    __APP_BUILD__: JSON.stringify({
      revision: git('rev-parse', 'HEAD'),
      committedAtUtc: git('show', '-s', '--format=%cI', 'HEAD'),
      builtAtUtc: command === 'build' ? new Date().toISOString() : null,
      dirty: Boolean(git('status', '--porcelain', '--untracked-files=no'))
    })
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5094',
      '/health': 'http://localhost:5094'
    }
  }
}));
