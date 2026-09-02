import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Builds straight into the ASP.NET host's wwwroot, so one `dotnet run` serves everything.
// Fixed filenames rather than hashed ones: the Razor page references them, and
// asp-append-version already busts the cache on content change.
export default defineConfig({
  plugins: [react()],
  base: '/admin-assets/',
  build: {
    outDir: '../Hubix.Web/wwwroot/admin-assets',
    emptyOutDir: true,
    rollupOptions: {
      output: {
        entryFileNames: 'index.js',
        chunkFileNames: '[name].js',
        assetFileNames: 'index.[ext]',
      },
    },
  },
  server: {
    port: 5200,
    // `npm run dev` talks to the running ASP.NET host for data and auth.
    proxy: {
      '/api': { target: 'http://localhost:5199', changeOrigin: true },
    },
  },
});
