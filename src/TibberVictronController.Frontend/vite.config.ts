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

export default defineConfig(({ command }) => {
  const build = {
    revision: git('rev-parse', 'HEAD'),
    committedAtUtc: git('show', '-s', '--format=%cI', 'HEAD'),
    builtAtUtc: command === 'build' ? new Date().toISOString() : null,
    dirty: Boolean(git('status', '--porcelain', '--untracked-files=no'))
  };
  return {
  plugins: [vue(), {
    // The same identity as a file next to index.html: an open page compares it with its own and reloads itself
    // after a deployment (src/appUpdate.ts, which builds the id the same way).
    name: 'efp-version-file',
    apply: 'build',
    generateBundle() {
      this.emitFile({ type: 'asset', fileName: 'version.json',
        source: JSON.stringify({ buildId: `${build.revision ?? 'unknown'}@${build.builtAtUtc}` }) });
    }
  }],
  // Embed the identity in the artifact so a browser reload never changes its build date.
  define: { __APP_BUILD__: JSON.stringify(build) },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5094',
      '/health': 'http://localhost:5094'
    }
  }
};
});
